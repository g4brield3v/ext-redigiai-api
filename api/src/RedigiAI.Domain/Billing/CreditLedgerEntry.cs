namespace RedigiAI.Domain.Billing;

/// <summary>Lançamento do livro-razão de créditos. Só inserção, nunca alteração.</summary>
public sealed class CreditLedgerEntry
{
    private CreditLedgerEntry() { }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public CreditSource Source { get; private set; }
    public int Delta { get; private set; }
    public Guid BucketId { get; private set; }
    public Guid? EssayId { get; private set; }
    public Guid? PaymentId { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static CreditLedgerEntry ForGrant(CreditBucket bucket, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            UserId = bucket.UserId,
            Source = bucket.Source,
            Delta = bucket.Granted,
            BucketId = bucket.Id,
            ExpiresAt = bucket.ExpiresAt,
            CreatedAt = now,
        };
}
