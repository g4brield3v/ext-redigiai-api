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

    /// <summary>O bucket de onde saiu a correção reservada para a redação; <c>null</c> se não houve reserva.</summary>
    Task<CreditBucket?> FindReservedBucketAsync(Guid essayId, CancellationToken ct);

    /// <summary>Devolve 1 correção ao bucket com <c>UPDATE … WHERE remaining &lt; granted</c>, na transação aberta.</summary>
    Task<bool> TryCreditBackAsync(Guid bucketId, CancellationToken ct);

    /// <summary>Entre as redações informadas, as que tiveram a correção devolvida ao saldo.</summary>
    Task<IReadOnlySet<Guid>> RefundedAsync(IReadOnlyCollection<Guid> essayIds, CancellationToken ct);

    /// <summary>Buckets com saldo e não vencidos.</summary>
    Task<IReadOnlyList<CreditBucket>> ListAvailableAsync(Guid userId, DateTimeOffset now, CancellationToken ct);
}
