using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Billing;
using RedigiAI.Domain.Essays;

namespace RedigiAI.Application.Essays;

/// <summary>
/// Encerra uma redação que falhou ou foi cancelada: muda o status e devolve a correção reservada, juntos.
/// Só age se a redação ainda está no status esperado, então rodar duas vezes não devolve o crédito duas vezes.
/// </summary>
internal sealed class EssayOutcomes(
    IEssayRepository essays, ICreditRepository credits, IAuditLog audit, IUnitOfWork unitOfWork, TimeProvider time)
{
    /// <returns><c>false</c> se outro processo já tinha avançado a redação.</returns>
    public Task<bool> EndWithRefundAsync(Guid essayId, EssayTransition transition, CancellationToken ct) =>
        unitOfWork.InTransactionAsync(
            async () =>
            {
                if (!await essays.TryMoveAsync(essayId, transition, ct))
                {
                    return false;
                }

                // A mudança de status vem antes da devolução na trilha: é a ordem que a linha do tempo mostra.
                audit.Record(
                    "system",
                    "essay.status_changed",
                    "essay",
                    essayId,
                    new { from = transition.From.ToString(), to = transition.To.ToString(), failureCode = transition.FailureCode });
                await RefundAsync(essayId, ct);
                return true;
            },
            ct);

    private async Task RefundAsync(Guid essayId, CancellationToken ct)
    {
        var bucket = await credits.FindReservedBucketAsync(essayId, ct);
        if (bucket is null)
        {
            return;
        }

        var now = time.GetUtcNow();
        var stillValid = bucket.ExpiresAt is null || bucket.ExpiresAt > now;
        if (stillValid && await credits.TryCreditBackAsync(bucket.Id, ct))
        {
            credits.AddEntry(CreditLedgerEntry.ForRefund(bucket, essayId, now));
        }
        else
        {
            // O bucket já venceu (plano depois do dia 1º): a correção volta como avulsa, sem vencimento.
            var replacement = CreditBucket.Grant(bucket.UserId, CreditSource.Single, 1, expiresAt: null, now);
            credits.AddGrant(replacement, CreditLedgerEntry.ForRefund(replacement, essayId, now));
        }

        audit.Record("system", "credit.refunded", "essay", essayId, new { bucketId = bucket.Id });
    }
}
