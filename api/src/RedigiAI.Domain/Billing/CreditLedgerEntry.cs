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

    /// <summary>Reserva de 1 correção no envio de uma redação.</summary>
    public static CreditLedgerEntry ForReservation(CreditBucket bucket, Guid essayId, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            UserId = bucket.UserId,
            Source = bucket.Source,
            Delta = -1,
            BucketId = bucket.Id,
            EssayId = essayId,
            ExpiresAt = bucket.ExpiresAt,
            CreatedAt = now,
        };

    /// <summary>Devolução da correção reservada para uma redação que falhou ou foi cancelada.</summary>
    public static CreditLedgerEntry ForRefund(CreditBucket bucket, Guid essayId, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            UserId = bucket.UserId,
            Source = CreditSource.Refund,
            Delta = 1,
            BucketId = bucket.Id,
            EssayId = essayId,
            ExpiresAt = bucket.ExpiresAt,
            CreatedAt = now,
        };

    /// <summary>Vencimento das correções que sobraram num bucket do plano (dia 1º): lançamento negativo.</summary>
    public static CreditLedgerEntry ForExpiry(CreditBucket bucket, int amount, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(amount, 1);
        return new()
        {
            Id = Guid.CreateVersion7(),
            UserId = bucket.UserId,
            Source = bucket.Source,
            Delta = -amount,
            BucketId = bucket.Id,
            ExpiresAt = bucket.ExpiresAt,
            CreatedAt = now,
        };
    }

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
