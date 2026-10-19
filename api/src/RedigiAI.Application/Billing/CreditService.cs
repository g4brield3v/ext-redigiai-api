using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Billing;

namespace RedigiAI.Application.Billing;

/// <param name="Actor">Quem concedeu, para a trilha de auditoria.</param>
public sealed record GrantCreditsCommand(Guid UserId, CreditSource Source, int Amount, DateTimeOffset? ExpiresAt, string Actor);

/// <summary>Resultado de uma passada do vencimento.</summary>
/// <param name="Done">Quantos itens foram concluídos agora.</param>
/// <param name="Failed">Os itens (assinatura ou bucket) que deram erro; a próxima passada tenta de novo.</param>
public sealed record CreditSweepResult(int Done, IReadOnlyList<Guid> Failed);

public interface ICreditService
{
    /// <summary>Concede correções: o bucket e o lançamento no ledger são gravados juntos.</summary>
    Task GrantAsync(GrantCreditsCommand command, CancellationToken ct);

    /// <summary>
    /// Vence as correções que sobraram nos buckets vencidos (as do plano, na data da cobrança seguinte), com lançamento
    /// negativo no ledger. Roda quantas vezes for preciso: cada bucket vence uma vez só.
    /// </summary>
    /// <returns>Quantos buckets venceram agora, e os que deram erro.</returns>
    Task<CreditSweepResult> ExpirePlanCreditsAsync(CancellationToken ct);
}

internal sealed class CreditService(
    ICreditRepository credits,
    IAuditLog audit,
    IUnitOfWork unitOfWork,
    TimeProvider time) : ICreditService
{
    private const int ExpiredBucketsPerSweep = 200;

    public async Task GrantAsync(GrantCreditsCommand command, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var bucket = CreditBucket.Grant(command.UserId, command.Source, command.Amount, command.ExpiresAt, now);

        credits.AddGrant(bucket, CreditLedgerEntry.ForGrant(bucket, now));
        audit.Record(
            command.Actor,
            "credit.granted",
            "credit_bucket",
            bucket.Id,
            new { source = command.Source.ToString().ToLowerInvariant(), delta = bucket.Granted });
        await unitOfWork.SaveChangesAsync(ct);
    }

    public async Task<CreditSweepResult> ExpirePlanCreditsAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var expired = 0;
        var failed = new List<Guid>();
        foreach (var bucket in await credits.ListExpiredWithBalanceAsync(now, ExpiredBucketsPerSweep, ct))
        {
            var done = await IsolatedAsync(
                bucket.Id,
                failed,
                () => unitOfWork.InTransactionAsync(
                    async () =>
                    {
                        var amount = await credits.TryExpireAsync(bucket.Id, now, ct);
                        if (amount == 0)
                        {
                            return false;
                        }

                        credits.AddEntry(CreditLedgerEntry.ForExpiry(bucket, amount, now));
                        audit.Record("system", "credit.expired", "credit_bucket", bucket.Id, new { delta = -amount });
                        return true;
                    },
                    ct),
                ct);
            expired += done ? 1 : 0;
        }

        return new CreditSweepResult(expired, failed);
    }

    /// <summary>
    /// Um item da varredura: se der erro (já desfeito pela transação), ele entra na lista de falhas e os seguintes
    /// continuam. Só o cancelamento da varredura interrompe.
    /// </summary>
    private static async Task<bool> IsolatedAsync(Guid itemId, List<Guid> failed, Func<Task<bool>> work, CancellationToken ct)
    {
        try
        {
            return await work();
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            failed.Add(itemId);
            return false;
        }
    }
}
