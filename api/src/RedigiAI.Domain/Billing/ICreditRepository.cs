namespace RedigiAI.Domain.Billing;

public interface ICreditRepository
{
    /// <summary>Concessão: bucket e lançamento no ledger gravados juntos.</summary>
    void AddGrant(CreditBucket bucket, CreditLedgerEntry entry);

    /// <summary>Buckets com saldo e não vencidos.</summary>
    Task<IReadOnlyList<CreditBucket>> ListAvailableAsync(Guid userId, DateTimeOffset now, CancellationToken ct);
}
