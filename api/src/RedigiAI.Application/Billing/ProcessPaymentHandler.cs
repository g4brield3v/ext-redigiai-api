using System.Text.Json;
using RedigiAI.Application.Abstractions;
using RedigiAI.Application.Notifications;
using RedigiAI.Domain.Billing;
using RedigiAI.Domain.Common;
using RedigiAI.Domain.Jobs;

namespace RedigiAI.Application.Billing;

/// <param name="MpPaymentId">O id do pagamento no Mercado Pago.</param>
/// <param name="EventId">A notificação que disparou o job, para marcá-la como processada; nulo quando veio da criação.</param>
public sealed record ProcessPaymentPayload(string MpPaymentId, Guid? EventId);

/// <summary>
/// <c>process_payment</c> (especificação, seções 7 e 9): consulta o pagamento na API do Mercado Pago e age pela
/// situação de lá, nunca pelo corpo do webhook nem pelo navegador (regra 19). Aprovado concede as correções;
/// estorno ou chargeback tiram as que ainda não foram usadas. Pode rodar várias vezes: cada mudança acontece uma vez.
/// </summary>
internal sealed class ProcessPaymentHandler(
    IPaymentGateway gateway,
    IPaymentRepository payments,
    ISubscriptionRepository subscriptions,
    ICreditRepository credits,
    PlanCredits planCredits,
    IWebhookEventRepository events,
    IJobQueue jobs,
    IAuditLog audit,
    IUnitOfWork unitOfWork,
    TimeProvider time) : IJobHandler
{
    public const int Attempts = 6;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Type => JobTypes.ProcessPayment;

    public async Task HandleAsync(string payload, CancellationToken ct)
    {
        var job = JsonSerializer.Deserialize<ProcessPaymentPayload>(payload, Json)!;
        var remote = await gateway.GetPaymentAsync(job.MpPaymentId, ct);
        // A compra avulsa vai lá com o id dela como referência; a cobrança do plano é achada pelo id do pagamento.
        var payment = remote is { ExternalReference: { } reference } && Guid.TryParse(reference, out var paymentId)
            ? await payments.FindAsync(paymentId, ct)
            : null;
        if (payment is null && remote is not null)
        {
            payment = await payments.FindByProviderIdAsync(remote.ProviderId, ct)
                ?? await RecordPlanChargeAsync(remote, ct);
        }

        if (remote is not null && payment is not null && (remote.AmountCents != payment.AmountCents || remote.Currency != "BRL"))
        {
            // Valor ou moeda diferente do calculado aqui: não concede nada e o job morre para conferência manual.
            audit.Record("system", "payment.mismatch", "payment", payment.Id, new { mpPaymentId = remote.ProviderId });
            await unitOfWork.SaveChangesAsync(ct);
            throw new AppError("PAYMENT_MISMATCH", 500, "Algo deu errado do nosso lado. Tente novamente em instantes.");
        }

        await unitOfWork.InTransactionAsync(
            async () =>
            {
                if (remote is not null && payment is not null)
                {
                    await ApplyAsync(payment, remote, ct);
                }

                if (job.EventId is { } eventId)
                {
                    await events.MarkProcessedAsync(eventId, time.GetUtcNow(), ct);
                }

                return true;
            },
            ct);
    }

    public Task OnDeadAsync(string payload, CancellationToken ct) =>
        // Nada a desfazer: nenhuma mudança fica pela metade. O job morto fica na fila para verificação manual.
        Task.CompletedTask;

    private async Task ApplyAsync(Payment payment, ProviderPayment remote, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        // A criação aceita lá sem a resposta chegar aqui é vinculada agora. Se a compra já está ligada a outro
        // pagamento lá, este não vale para ela.
        if (payment.MpPaymentId != remote.ProviderId
            && (payment.MpPaymentId is not null
                || !await payments.TryAttachProviderAsync(payment.Id, remote.ProviderId, null, null, null, now, ct)))
        {
            audit.Record("system", "payment.provider_mismatch", "payment", payment.Id, new { mpPaymentId = remote.ProviderId });
            return;
        }

        if (remote.Status == payment.Status)
        {
            if (payment.Status == PaymentStatus.Approved)
            {
                await ApplyPartialRefundAsync(payment, remote, now, ct);
            }

            return;
        }

        if (!await payments.TryTransitionAsync(payment.Id, remote.Status, now, ct))
        {
            if (remote.Status == PaymentStatus.Approved && payment.Status is PaymentStatus.Cancelled or PaymentStatus.Rejected)
            {
                // Pago lá depois de cancelado ou recusado aqui: o dinheiro entrou sem correções. Para conferência manual.
                audit.Record(
                    "system", "payment.approved_after_end", "payment", payment.Id, new { mpPaymentId = remote.ProviderId, from = Name(payment.Status) });
            }

            return;
        }

        var data = new { mpPaymentId = remote.ProviderId, from = Name(payment.Status), to = Name(remote.Status) };
        switch (remote.Status)
        {
            case PaymentStatus.Approved:
                var notify = true;
                if (payment.Kind == PaymentKind.Plan)
                {
                    notify = await ApprovePlanChargeAsync(payment, now, ct);
                }
                else
                {
                    var bucket = CreditBucket.GrantForPayment(payment, now);
                    credits.AddGrant(bucket, CreditLedgerEntry.ForPaymentGrant(bucket, payment.Id, now));
                    audit.Record(
                        "system", "credit.granted", "credit_bucket", bucket.Id, new { source = "single", delta = bucket.Granted, paymentId = payment.Id });
                }

                audit.Record("system", "payment.approved", "payment", payment.Id, data);
                if (notify)
                {
                    EmailJobs.Enqueue(jobs, EmailTemplates.PaymentApproved, payment.Id);
                }
                break;

            case PaymentStatus.Refunded or PaymentStatus.ChargedBack:
                var removed = 0;
                if (await credits.FindByGrantKeyAsync(CreditBucket.PaymentGrantKey(payment.Id), ct) is { } granted)
                {
                    removed = await credits.RevokeAsync(granted.Id, now, ct);
                    if (removed > 0)
                    {
                        credits.AddEntry(CreditLedgerEntry.ForPaymentReversal(granted, removed, payment.Id, now));
                    }
                }

                audit.Record("system", $"payment.{Name(remote.Status)}", "payment", payment.Id, new { data.mpPaymentId, data.from, creditsRemoved = removed });
                break;

            default:
                audit.Record("system", $"payment.{Name(remote.Status)}", "payment", payment.Id, data);
                if (remote.Status == PaymentStatus.Rejected && payment is { Kind: PaymentKind.Plan, SubscriptionId: { } subscriptionId })
                {
                    // Cobrança do plano recusada: sem renovação até regularizar; as correções que o aluno tem seguem valendo.
                    if (await subscriptions.TryTransitionAsync(subscriptionId, SubscriptionStatus.Paused, now, ct))
                    {
                        audit.Record("system", "subscription.paused", "subscription", subscriptionId, new { paymentId = payment.Id });
                    }

                    EmailJobs.Enqueue(jobs, EmailTemplates.SubscriptionPaymentFailed, payment.Id);
                }

                break;
        }
    }

    /// <summary>
    /// O aviso da cobrança (<c>subscription_authorized_payment</c>) se perdeu, mas o do pagamento chegou: a referência
    /// de lá é a assinatura daqui, e a compra do plano nasce agora. Valor e moeda são conferidos depois, como sempre.
    /// </summary>
    private async Task<Payment?> RecordPlanChargeAsync(ProviderPayment remote, CancellationToken ct)
    {
        if (!Guid.TryParse(remote.ExternalReference, out var subscriptionId)
            || await subscriptions.FindAsync(subscriptionId, ct) is not { MpPreapprovalId: not null } subscription)
        {
            return null;
        }

        try
        {
            var payment = Payment.StartPlanCharge(subscription, remote.ProviderId, time.GetUtcNow());
            payments.Add(payment);
            audit.Record("system", "payment.started", "payment", payment.Id, new { kind = "plan", subscriptionId });
            await unitOfWork.SaveChangesAsync(ct);
        }
        catch (UniqueConflictException)
        {
            // Outro job registrou a mesma cobrança antes.
        }

        return await payments.FindByProviderIdAsync(remote.ProviderId, ct);
    }

    /// <summary>
    /// A cobrança do plano aprovada: zera o ciclo anterior e concede as 12 correções até a cobrança seguinte, e a
    /// assinatura pausada por uma recusa volta a ficar ativa.
    /// </summary>
    /// <returns><c>false</c> se não renovou (assinatura duplicada).</returns>
    private async Task<bool> ApprovePlanChargeAsync(Payment payment, DateTimeOffset now, CancellationToken ct)
    {
        var subscription = payment.SubscriptionId is { } id ? await subscriptions.FindAsync(id, ct) : null;
        if (subscription is not null
            && await subscriptions.FindOpenAsync(payment.UserId, ct) is { } open && open.Id != subscription.Id)
        {
            // Cobrança de uma assinatura duplicada (cancelada ao ser autorizada): o dinheiro entrou, mas o aluno já tem
            // plano. Não renova; fica para estorno manual.
            audit.Record("system", "payment.duplicate_subscription", "payment", payment.Id, new { subscriptionId = subscription.Id });
            return false;
        }

        // A próxima cobrança que o Mercado Pago informou; sem ela (ou já passada), um mês depois da aprovação.
        var cycleEnd = subscription?.NextBillingAt is { } next && next > now.AddDays(1) ? next : now.AddMonths(1);
        await planCredits.RenewAsync(payment, cycleEnd, now, ct);

        if (subscription is not null && await subscriptions.TryTransitionAsync(subscription.Id, SubscriptionStatus.Active, now, ct))
        {
            audit.Record("system", "subscription.active", "subscription", subscription.Id, new { paymentId = payment.Id });
        }

        return true;
    }

    /// <summary>
    /// Estorno parcial (a compra continua aprovada lá): saem as correções inteiras que o valor estornado cobre, sem
    /// arredondar, contando o que já foi estornado antes. Sobra que não fecha uma correção fica no audit_log.
    /// </summary>
    private async Task ApplyPartialRefundAsync(Payment payment, ProviderPayment remote, DateTimeOffset now, CancellationToken ct)
    {
        if (remote.RefundedCents <= payment.RefundedCents
            || !await payments.TryRecordRefundAsync(payment.Id, payment.RefundedCents, remote.RefundedCents, now, ct))
        {
            return;
        }

        var toRemove = payment.CreditsCoveredBy(remote.RefundedCents) - payment.CreditsCoveredBy(payment.RefundedCents);
        var removed = 0;
        if (toRemove > 0 && await credits.FindByGrantKeyAsync(CreditBucket.PaymentGrantKey(payment.Id), ct) is { } granted)
        {
            removed = await credits.RemoveAsync(granted.Id, toRemove, ct);
            if (removed > 0)
            {
                credits.AddEntry(CreditLedgerEntry.ForPaymentReversal(granted, removed, payment.Id, now));
            }
        }

        audit.Record(
            "system",
            "payment.partially_refunded",
            "payment",
            payment.Id,
            new
            {
                mpPaymentId = remote.ProviderId,
                refundedCents = remote.RefundedCents,
                creditsRemoved = removed,
                // Parte do valor estornado que não fecha uma correção inteira: para conferência manual.
                uncoveredCents = remote.RefundedCents % payment.CreditCents,
            });
    }

    private static string Name(PaymentStatus status) => JsonNamingPolicy.SnakeCaseLower.ConvertName(status.ToString());
}
