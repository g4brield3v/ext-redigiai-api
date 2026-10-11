namespace RedigiAI.Domain.Billing;

public interface ICreditRepository
{
    /// <summary>Concessão: bucket e lançamento no ledger gravados juntos.</summary>
    void AddGrant(CreditBucket bucket, CreditLedgerEntry entry);

    /// <summary>
    /// Debita 1 correção com <c>UPDATE … WHERE remaining &gt;= 1</c> (regra 12). Executa na hora, dentro da
    /// transação aberta pelo caso de uso.
    /// </summary>
    /// <returns><c>false</c> se o bucket ficou sem saldo ou venceu nesse meio-tempo.</returns>
    Task<bool> TryDebitAsync(Guid bucketId, DateTimeOffset now, CancellationToken ct);

    void AddEntry(CreditLedgerEntry entry);

    /// <summary>Buckets com saldo e não vencidos.</summary>
    Task<IReadOnlyList<CreditBucket>> ListAvailableAsync(Guid userId, DateTimeOffset now, CancellationToken ct);
}
