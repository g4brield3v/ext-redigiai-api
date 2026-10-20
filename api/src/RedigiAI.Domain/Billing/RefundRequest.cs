namespace RedigiAI.Domain.Billing;

public enum RefundStatus
{
    /// <summary>Pedido feito: as correções não usadas já saíram do saldo; o reembolso está na fila.</summary>
    Requested,

    /// <summary>Reembolso feito no Mercado Pago.</summary>
    Paid,
}

/// <summary>
/// Direito de arrependimento (CDC, art. 49; especificação, seção 15): em até 7 dias da compra aprovada, o aluno desiste
/// e recebe o valor das correções que não usou. Um pedido por compra.
/// </summary>
public sealed class RefundRequest
{
    public const int MaxReasonLength = 500;

    private RefundRequest() { }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid PaymentId { get; private set; }
    public RefundStatus Status { get; private set; }
    public long AmountCents { get; private set; }
    public int CreditsRemoved { get; private set; }
    public string? Reason { get; private set; }

    /// <summary>O id do reembolso no Mercado Pago, quando ele é feito.</summary>
    public string? MpRefundId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }

    /// <summary>
    /// Quanto devolver por <paramref name="unused"/> correções não usadas da compra: R$ 16 cada na avulsa; no plano,
    /// R$ 39,90 menos R$ 3,33 por correção usada ou em andamento (decisão de 18/10/2026), nunca abaixo de zero. Nunca
    /// mais do que ainda não foi estornado da compra.
    /// </summary>
    public static long AmountFor(Payment payment, int unused)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(unused);
        var amount = payment.Kind == PaymentKind.Single
            ? unused * Payment.SinglePriceCents
            : payment.AmountCents - (Math.Max(0, payment.Quantity - unused) * Payment.PlanCreditCents);
        return Math.Clamp(amount, 0, Math.Max(0, payment.AmountCents - payment.RefundedCents));
    }

    public static RefundRequest Create(Payment payment, int creditsRemoved, long amountCents, string? reason, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(amountCents, 1);
        if (reason is { Length: > MaxReasonLength })
        {
            throw new ArgumentException("Reason too long.", nameof(reason));
        }

        return new RefundRequest
        {
            Id = Guid.CreateVersion7(),
            UserId = payment.UserId,
            PaymentId = payment.Id,
            Status = RefundStatus.Requested,
            AmountCents = amountCents,
            CreditsRemoved = creditsRemoved,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            CreatedAt = now,
        };
    }
}

public interface IRefundRequestRepository
{
    void Add(RefundRequest request);

    Task<RefundRequest?> FindAsync(Guid refundRequestId, CancellationToken ct);

    Task<RefundRequest?> FindByPaymentAsync(Guid paymentId, CancellationToken ct);

    /// <summary>As compras, entre as informadas, que já têm pedido de arrependimento.</summary>
    Task<IReadOnlySet<Guid>> PaymentsWithRequestAsync(IReadOnlyCollection<Guid> paymentIds, CancellationToken ct);

    /// <summary>Marca o reembolso como feito: <c>UPDATE … WHERE status = 'requested'</c> (regra 10).</summary>
    Task<bool> TryMarkPaidAsync(Guid refundRequestId, string mpRefundId, DateTimeOffset now, CancellationToken ct);
}
