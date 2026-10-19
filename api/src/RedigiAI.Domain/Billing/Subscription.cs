namespace RedigiAI.Domain.Billing;

public enum SubscriptionStatus
{
    Active,
    Paused,
    Cancelled,
}

/// <summary>
/// Plano mensal, cobrado no cartão pelo Mercado Pago (preapproval). Especificação, seção 9: cada cobrança aprovada dá
/// 12 correções, que vencem na cobrança seguinte.
/// </summary>
public sealed class Subscription
{
    private const int MaxPreapprovalIdLength = 100;

    private Subscription() { }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string MpPreapprovalId { get; private set; } = null!;
    public SubscriptionStatus Status { get; private set; }
    public DateTimeOffset? NextBillingAt { get; private set; }
    public string? CardLast4 { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Assinatura autorizada no Mercado Pago: começa ativa.</summary>
    public static Subscription Start(
        Guid userId, string mpPreapprovalId, DateTimeOffset? nextBillingAt, string? cardLast4, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(mpPreapprovalId) || mpPreapprovalId.Length > MaxPreapprovalIdLength)
        {
            throw new ArgumentException("Invalid preapproval id.", nameof(mpPreapprovalId));
        }

        if (cardLast4 is not null && (cardLast4.Length != 4 || !cardLast4.All(char.IsAsciiDigit)))
        {
            throw new ArgumentException("Card last digits must be 4 digits.", nameof(cardLast4));
        }

        return new Subscription
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            MpPreapprovalId = mpPreapprovalId,
            Status = SubscriptionStatus.Active,
            NextBillingAt = nextBillingAt,
            CardLast4 = cardLast4,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

}
