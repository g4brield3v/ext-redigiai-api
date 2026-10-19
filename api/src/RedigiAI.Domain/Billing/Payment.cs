namespace RedigiAI.Domain.Billing;

public enum PaymentKind
{
    Single,
    Plan,
}

public enum PaymentMethod
{
    Pix,
    Card,
}

public enum PaymentStatus
{
    Pending,
    Approved,
    Rejected,
    Cancelled,
    Refunded,
    ChargedBack,
}

/// <summary>
/// Uma compra no Mercado Pago (especificação, seção 9). O valor é sempre calculado aqui, nunca vindo do navegador; a
/// situação só muda pelo que a API do Mercado Pago informa, com <c>UPDATE</c> condicional no repositório (regra 10).
/// </summary>
public sealed class Payment
{
    public const long SinglePriceCents = 1600;
    public const int MaxQuantity = 10;
    public const int MaxKeyLength = 100;

    /// <summary>Validade do QR code do Pix.</summary>
    public static readonly TimeSpan PixValidity = TimeSpan.FromMinutes(30);

    /// <summary>Direito de arrependimento (CDC, art. 49): 7 dias corridos do pagamento aprovado.</summary>
    public static readonly TimeSpan RefundWindow = TimeSpan.FromDays(7);

    private Payment() { }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }

    /// <summary>O id do pagamento no Mercado Pago; nulo até a criação lá responder.</summary>
    public string? MpPaymentId { get; private set; }
    public PaymentKind Kind { get; private set; }
    public PaymentMethod Method { get; private set; }
    public long AmountCents { get; private set; }
    public int Quantity { get; private set; }
    public PaymentStatus Status { get; private set; }

    /// <summary>A <c>Idempotency-Key</c> do pedido: repetir a chave devolve esta compra.</summary>
    public string IdempotencyKey { get; private set; } = null!;

    /// <summary>Quando o comprador de 16 ou 17 anos declarou a autorização do responsável.</summary>
    public DateTimeOffset? GuardianAuthorizedAt { get; private set; }
    public string? PixQrCode { get; private set; }
    public string? PixQrCodeBase64 { get; private set; }
    public DateTimeOffset? PixExpiresAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? ApprovedAt { get; private set; }

    /// <summary>Correção avulsa: R$ 16 por correção, de 1 a 10, por Pix ou cartão.</summary>
    public static Payment StartSingle(
        Guid userId, PaymentMethod method, int quantity, string idempotencyKey, DateTimeOffset? guardianAuthorizedAt, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(quantity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quantity, MaxQuantity);
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > MaxKeyLength)
        {
            throw new ArgumentException("Invalid idempotency key.", nameof(idempotencyKey));
        }

        return new Payment
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Kind = PaymentKind.Single,
            Method = method,
            AmountCents = SinglePriceCents * quantity,
            Quantity = quantity,
            Status = PaymentStatus.Pending,
            IdempotencyKey = idempotencyKey,
            GuardianAuthorizedAt = guardianAuthorizedAt,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// As situações de onde se pode chegar a <paramref name="to"/>. Pendente vira aprovado, recusado ou cancelado
    /// (Pix vencido); aprovado vira estornado ou contestado (chargeback). Pendente também pode virar estornado ou
    /// contestado direto, quando a aprovação e o estorno acontecem lá antes de processarmos a aprovação aqui: nada
    /// foi concedido, nada sai. As outras são finais.
    /// </summary>
    public static IReadOnlyList<PaymentStatus> ReachableFrom(PaymentStatus to) => to switch
    {
        PaymentStatus.Approved or PaymentStatus.Rejected or PaymentStatus.Cancelled => [PaymentStatus.Pending],
        PaymentStatus.Refunded or PaymentStatus.ChargedBack => [PaymentStatus.Approved, PaymentStatus.Pending],
        _ => [],
    };

    /// <summary>Dentro dos 7 dias do direito de arrependimento.</summary>
    public bool RefundableOn(DateTimeOffset now) =>
        Status == PaymentStatus.Approved && ApprovedAt is { } approved && now < approved + RefundWindow;
}
