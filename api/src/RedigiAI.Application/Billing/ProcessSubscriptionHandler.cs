using System.Text.Json;
using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Billing;
using RedigiAI.Domain.Jobs;

namespace RedigiAI.Application.Billing;

/// <summary>O que o Mercado Pago avisou de uma assinatura.</summary>
public enum SubscriptionNotice
{
    /// <summary>A assinatura (preapproval) mudou: autorizada, pausada, cancelada.</summary>
    Subscription,

    /// <summary>Uma cobrança mensal da assinatura foi feita (ou tentada).</summary>
    Charge,
}

/// <param name="ProviderId">O id da assinatura ou da cobrança no Mercado Pago.</param>
/// <param name="EventId">A notificação que disparou o job; nulo quando veio da reconciliação.</param>
public sealed record ProcessSubscriptionPayload(SubscriptionNotice Notice, string ProviderId, Guid? EventId);

/// <summary>
/// <c>process_subscription</c>: consulta a assinatura ou a cobrança no Mercado Pago e age pelo que está lá, nunca pelo
/// corpo do aviso (regra 19). A assinatura muda de situação; cada cobrança vira uma compra do plano, que o
/// <c>process_payment</c> aprova (e concede as 12 correções) como qualquer outra compra.
/// </summary>
internal sealed class ProcessSubscriptionHandler(
    IPaymentGateway gateway,
    ISubscriptionRepository subscriptions,
    IPaymentRepository payments,
    IWebhookEventRepository events,
    IJobQueue jobs,
    IAuditLog audit,
    IUnitOfWork unitOfWork,
    TimeProvider time) : IJobHandler
{
    public const int Attempts = 6;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Type => JobTypes.ProcessSubscription;

    public async Task HandleAsync(string payload, CancellationToken ct)
    {
        var job = JsonSerializer.Deserialize<ProcessSubscriptionPayload>(payload, Json)!;
        if (job.Notice == SubscriptionNotice.Subscription)
        {
            await ApplySubscriptionAsync(job.ProviderId, ct);
        }
        else
        {
            await RecordChargeAsync(job.ProviderId, ct);
        }

        if (job.EventId is { } eventId)
        {
            await events.MarkProcessedAsync(eventId, time.GetUtcNow(), ct);
        }
    }

    public Task OnDeadAsync(string payload, CancellationToken ct) =>
        // Nada fica pela metade; o job morto fica na fila para verificação manual.
        Task.CompletedTask;

    private async Task ApplySubscriptionAsync(string providerId, CancellationToken ct)
    {
        if (await gateway.GetSubscriptionAsync(providerId, ct) is not { } remote)
        {
            return;
        }

        var subscription = await subscriptions.FindByProviderIdAsync(providerId, ct)
            ?? (Guid.TryParse(remote.ExternalReference, out var id) ? await subscriptions.FindAsync(id, ct) : null);
        if (subscription is null)
        {
            return;
        }

        var now = time.GetUtcNow();
        if (remote.Status == SubscriptionStatus.Active && subscription.Status == SubscriptionStatus.Pending
            && await subscriptions.FindOpenAsync(subscription.UserId, ct) is { } open && open.Id != subscription.Id)
        {
            await CancelDuplicateAsync(subscription, providerId, ct);
            return;
        }

        try
        {
            await unitOfWork.InTransactionAsync(
                async () =>
                {
                    if (subscription.MpPreapprovalId != providerId
                        && (subscription.MpPreapprovalId is not null
                            || !await subscriptions.TryAttachProviderAsync(subscription.Id, providerId, now, ct)))
                    {
                        audit.Record("system", "subscription.provider_mismatch", "subscription", subscription.Id, new { mpPreapprovalId = providerId });
                        return false;
                    }

                    await subscriptions.UpdateBillingAsync(subscription.Id, remote.NextChargeAt, remote.CardLast4, now, ct);

                    // Lá a assinatura segue "autorizada" enquanto o cartão é cobrado de novo; aqui, a pausa por cobrança
                    // recusada só acaba com uma cobrança aprovada (process_payment). Por isso "ativa" só vale da pendente.
                    var to = remote.Status;
                    if (to == SubscriptionStatus.Active && subscription.Status != SubscriptionStatus.Pending)
                    {
                        return true;
                    }

                    if (to != subscription.Status && await subscriptions.TryTransitionAsync(subscription.Id, to, now, ct))
                    {
                        audit.Record(
                            "system",
                            $"subscription.{JsonNamingPolicy.SnakeCaseLower.ConvertName(to.ToString())}",
                            "subscription",
                            subscription.Id,
                            new { mpPreapprovalId = providerId });
                    }

                    return true;
                },
                ct);
        }
        catch (UniqueConflictException)
        {
            // A outra assinatura do aluno foi ativada no mesmo instante.
            await CancelDuplicateAsync(subscription, providerId, ct);
        }
    }

    /// <summary>
    /// Autorizada lá com outra assinatura do aluno já aberta aqui (dois checkouts concluídos): cancelada lá, para não
    /// ser cobrada todo mês, e aqui. Uma cobrança que já tenha passado fica no audit_log para estorno manual.
    /// </summary>
    private async Task CancelDuplicateAsync(Subscription subscription, string providerId, CancellationToken ct)
    {
        await gateway.CancelSubscriptionAsync(providerId, ct);
        var now = time.GetUtcNow();
        await unitOfWork.InTransactionAsync(
            async () =>
            {
                if (subscription.MpPreapprovalId is null)
                {
                    await subscriptions.TryAttachProviderAsync(subscription.Id, providerId, now, ct);
                }

                if (await subscriptions.TryTransitionAsync(subscription.Id, SubscriptionStatus.Cancelled, now, ct))
                {
                    audit.Record("system", "subscription.duplicate", "subscription", subscription.Id, new { mpPreapprovalId = providerId });
                }

                return true;
            },
            ct);
    }

    private async Task RecordChargeAsync(string chargeId, CancellationToken ct)
    {
        if (await gateway.GetSubscriptionChargeAsync(chargeId, ct) is not { PaymentId: { } paymentId } charge
            || await subscriptions.FindByProviderIdAsync(charge.PreapprovalId, ct) is not { } subscription)
        {
            // Cobrança agendada, ainda sem pagamento, ou de uma assinatura que não é daqui.
            return;
        }

        try
        {
            await unitOfWork.InTransactionAsync(
                async () =>
                {
                    if (await payments.FindByProviderIdAsync(paymentId, ct) is null)
                    {
                        var payment = Payment.StartPlanCharge(subscription, paymentId, time.GetUtcNow());
                        payments.Add(payment);
                        audit.Record("system", "payment.started", "payment", payment.Id, new { kind = "plan", subscriptionId = subscription.Id });
                    }

                    // A situação da cobrança vem da consulta ao pagamento, no job de sempre.
                    jobs.Enqueue(JobTypes.ProcessPayment, new ProcessPaymentPayload(paymentId, EventId: null), ProcessPaymentHandler.Attempts);
                    return true;
                },
                ct);
        }
        catch (UniqueConflictException)
        {
            // Outro job registrou a mesma cobrança antes: a consulta dele já está na fila.
        }
    }
}
