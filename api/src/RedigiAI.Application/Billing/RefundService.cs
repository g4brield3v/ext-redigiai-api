using System.Text.Json;
using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Accounts;
using RedigiAI.Domain.Billing;
using RedigiAI.Domain.Common;
using RedigiAI.Domain.Jobs;

namespace RedigiAI.Application.Billing;

/// <param name="Reason">Por que desistiu, opcional, até 500 caracteres.</param>
public sealed record RefundRequestBody(string? Reason);

public sealed record RefundRequestDto(
    Guid Id, Guid PaymentId, RefundStatus Status, long AmountCents, int CreditsRemoved, DateTimeOffset CreatedAt);

public interface IRefundService
{
    /// <summary>
    /// Direito de arrependimento: em até 7 dias da compra aprovada, tira do saldo as correções não usadas dela e põe na
    /// fila o reembolso do valor correspondente. No plano, a assinatura é cancelada. Pedir de novo devolve o mesmo
    /// pedido. Compra de outro aluno: 404.
    /// </summary>
    Task<RefundRequestDto> RequestAsync(string clerkUserId, Guid paymentId, string? reason, CancellationToken ct);
}

internal sealed class RefundService(
    IUserRepository users,
    IPaymentRepository payments,
    ISubscriptionRepository subscriptions,
    ICreditRepository credits,
    IRefundRequestRepository refunds,
    IPaymentGateway gateway,
    IJobQueue jobs,
    IAuditLog audit,
    IUnitOfWork unitOfWork,
    TimeProvider time) : IRefundService
{
    public async Task<RefundRequestDto> RequestAsync(string clerkUserId, Guid paymentId, string? reason, CancellationToken ct)
    {
        var user = await users.FindByClerkIdAsync(clerkUserId, ct) ?? throw Errors.ProfileNotFound();
        var payment = await payments.FindAsync(paymentId, ct);
        if (payment is null || payment.UserId != user.Id || payment.MpPaymentId is null)
        {
            throw Errors.NotFound();
        }

        if (reason is { Length: > RefundRequest.MaxReasonLength } || (reason?.Any(c => char.IsControl(c) && c is not '\n' and not '\r') ?? false))
        {
            throw Errors.Validation($"Escreva o motivo em até {RefundRequest.MaxReasonLength} caracteres.");
        }

        if (await refunds.FindByPaymentAsync(payment.Id, ct) is { } existing)
        {
            return ToDto(existing);
        }

        var now = time.GetUtcNow();
        if (payment.Status != PaymentStatus.Approved)
        {
            throw Errors.PaymentInvalidState();
        }

        if (!payment.RefundableOn(now))
        {
            throw Errors.RefundWindowExpired();
        }

        // O que dá para devolver, pelo saldo de agora; o valor final sai do que a trava do bucket tirar de fato.
        var bucket = await credits.FindByGrantKeyAsync(CreditBucket.PaymentGrantKey(payment.Id), ct);
        if (bucket is null || RefundRequest.AmountFor(payment, bucket.Remaining) == 0)
        {
            throw Errors.NothingToRefund();
        }

        // No plano, a assinatura é cancelada lá primeiro: se o Mercado Pago não responder, nada muda aqui.
        var subscription = payment is { Kind: PaymentKind.Plan, SubscriptionId: { } subscriptionId }
            ? await subscriptions.FindAsync(subscriptionId, ct)
            : null;
        if (subscription is { Status: SubscriptionStatus.Active or SubscriptionStatus.Paused, MpPreapprovalId: { } preapprovalId })
        {
            await gateway.CancelSubscriptionAsync(preapprovalId, ct);
        }

        try
        {
            var request = await unitOfWork.InTransactionAsync(
                async () =>
                {
                    // Trava o bucket: um envio de redação no mesmo instante ou usa a correção, ou ela sai aqui.
                    var removed = await credits.RevokeAsync(bucket.Id, now, ct);
                    var amount = RefundRequest.AmountFor(payment, removed);
                    if (amount == 0)
                    {
                        throw Errors.NothingToRefund();
                    }

                    if (removed > 0)
                    {
                        credits.AddEntry(CreditLedgerEntry.ForPaymentReversal(bucket, removed, payment.Id, now));
                    }

                    var created = RefundRequest.Create(payment, removed, amount, reason, now);
                    refunds.Add(created);
                    if (subscription is not null && await subscriptions.TryTransitionAsync(subscription.Id, SubscriptionStatus.Cancelled, now, ct))
                    {
                        audit.Record($"user:{user.Id}", "subscription.cancelled", "subscription", subscription.Id, new { refundRequestId = created.Id });
                    }

                    audit.Record(
                        $"user:{user.Id}",
                        "refund.requested",
                        "refund_request",
                        created.Id,
                        new { paymentId = payment.Id, amountCents = amount, creditsRemoved = removed });
                    jobs.Enqueue(JobTypes.ProcessRefund, new ProcessRefundPayload(created.Id), ProcessRefundHandler.Attempts);
                    return created;
                },
                ct);
            return ToDto(request);
        }
        catch (Exception ex) when (ex is UniqueConflictException || ex is AppError { Code: "NOTHING_TO_REFUND" })
        {
            // Outro pedido da mesma compra chegou primeiro (e revogou o bucket enquanto este esperava a trava): vale o
            // dele. Sem pedido nenhum, era mesmo nada a devolver.
            return await refunds.FindByPaymentAsync(payment.Id, ct) is { } winner ? ToDto(winner) : throw Errors.NothingToRefund();
        }
    }

    private static RefundRequestDto ToDto(RefundRequest request) =>
        new(request.Id, request.PaymentId, request.Status, request.AmountCents, request.CreditsRemoved, request.CreatedAt);
}

public sealed record ProcessRefundPayload(Guid RefundRequestId);

/// <summary>
/// <c>process_refund</c>: faz o reembolso no Mercado Pago, com o id do pedido como chave de idempotência, e o marca como
/// feito. As correções já saíram do saldo no pedido; a notificação do estorno que o Mercado Pago manda depois não tira
/// mais nada (o bucket está revogado).
/// </summary>
internal sealed class ProcessRefundHandler(
    IRefundRequestRepository refunds,
    IPaymentRepository payments,
    IPaymentGateway gateway,
    IAuditLog audit,
    IUnitOfWork unitOfWork,
    TimeProvider time) : IJobHandler
{
    public const int Attempts = 6;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Type => JobTypes.ProcessRefund;

    public async Task HandleAsync(string payload, CancellationToken ct)
    {
        var job = JsonSerializer.Deserialize<ProcessRefundPayload>(payload, Json)!;
        if (await refunds.FindAsync(job.RefundRequestId, ct) is not { Status: RefundStatus.Requested } request
            || await payments.FindAsync(request.PaymentId, ct) is not { MpPaymentId: { } mpPaymentId })
        {
            return;
        }

        var refundId = await gateway.RefundAsync(mpPaymentId, request.AmountCents, request.Id, ct);
        await unitOfWork.InTransactionAsync(
            async () =>
            {
                if (await refunds.TryMarkPaidAsync(request.Id, refundId, time.GetUtcNow(), ct))
                {
                    audit.Record("system", "refund.paid", "refund_request", request.Id, new { mpRefundId = refundId, amountCents = request.AmountCents });
                }

                return true;
            },
            ct);
    }

    public async Task OnDeadAsync(string payload, CancellationToken ct)
    {
        // O valor não voltou ao aluno: fica para verificação manual.
        var job = JsonSerializer.Deserialize<ProcessRefundPayload>(payload, Json)!;
        audit.Record("system", "refund.failed", "refund_request", job.RefundRequestId);
        await unitOfWork.SaveChangesAsync(ct);
    }
}
