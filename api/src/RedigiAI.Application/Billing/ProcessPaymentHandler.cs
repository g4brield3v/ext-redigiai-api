using System.Text.Json;
using RedigiAI.Application.Abstractions;
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
    ICreditRepository credits,
    IWebhookEventRepository events,
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
        var payment = remote is { ExternalReference: { } reference } && Guid.TryParse(reference, out var paymentId)
            ? await payments.FindAsync(paymentId, ct)
            : null;

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
        if (payment.MpPaymentId is null)
        {
            // A criação foi aceita lá, mas a resposta não chegou aqui.
            await payments.TryAttachProviderAsync(payment.Id, remote.ProviderId, null, null, null, now, ct);
        }
        else if (payment.MpPaymentId != remote.ProviderId)
        {
            return;
        }

        if (remote.Status == payment.Status || !await payments.TryTransitionAsync(payment.Id, remote.Status, now, ct))
        {
            return;
        }

        var data = new { mpPaymentId = remote.ProviderId, from = Name(payment.Status), to = Name(remote.Status) };
        switch (remote.Status)
        {
            case PaymentStatus.Approved:
                var bucket = CreditBucket.GrantForPayment(payment, now);
                credits.AddGrant(bucket, CreditLedgerEntry.ForPaymentGrant(bucket, payment.Id, now));
                audit.Record("system", "payment.approved", "payment", payment.Id, data);
                audit.Record(
                    "system", "credit.granted", "credit_bucket", bucket.Id, new { source = "single", delta = bucket.Granted, paymentId = payment.Id });
                break;

            case PaymentStatus.Refunded or PaymentStatus.ChargedBack:
                var removed = 0;
                if (await credits.FindByGrantKeyAsync(CreditBucket.PaymentGrantKey(payment.Id), ct) is { } granted)
                {
                    removed = await credits.TryRevokeRemainingAsync(granted.Id, ct);
                    if (removed > 0)
                    {
                        credits.AddEntry(CreditLedgerEntry.ForPaymentReversal(granted, removed, payment.Id, now));
                    }
                }

                audit.Record("system", $"payment.{Name(remote.Status)}", "payment", payment.Id, new { data.mpPaymentId, data.from, creditsRemoved = removed });
                break;

            default:
                audit.Record("system", $"payment.{Name(remote.Status)}", "payment", payment.Id, data);
                break;
        }
    }

    private static string Name(PaymentStatus status) => JsonNamingPolicy.SnakeCaseLower.ConvertName(status.ToString());
}
