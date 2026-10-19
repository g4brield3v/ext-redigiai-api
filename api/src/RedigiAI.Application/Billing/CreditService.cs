using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Billing;

namespace RedigiAI.Application.Billing;

/// <param name="Actor">Quem concedeu, para a trilha de auditoria.</param>
public sealed record GrantCreditsCommand(Guid UserId, CreditSource Source, int Amount, DateTimeOffset? ExpiresAt, string Actor);

public interface ICreditService
{
    /// <summary>Concede correções: o bucket e o lançamento no ledger são gravados juntos.</summary>
    Task GrantAsync(GrantCreditsCommand command, CancellationToken ct);

    /// <summary>
    /// Segunda-feira 00h (Brasília): concede 3 correções a cada assinatura ativa, com vencimento no próximo dia 1º.
    /// Roda quantas vezes for preciso na semana: cada assinatura recebe a semana uma vez só.
    /// </summary>
    /// <returns>Quantas assinaturas receberam agora.</returns>
    Task<int> GrantWeeklyPlanCreditsAsync(CancellationToken ct);

    /// <summary>Dia 1º 00h (Brasília): vence as correções do plano que sobraram, com lançamento negativo no ledger.</summary>
    /// <returns>Quantos buckets venceram agora.</returns>
    Task<int> ExpirePlanCreditsAsync(CancellationToken ct);
}

internal sealed class CreditService(
    ICreditRepository credits,
    ISubscriptionRepository subscriptions,
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

    public async Task<int> GrantWeeklyPlanCreditsAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var weekStart = PlanCalendar.WeekStart(now);
        if (PlanCalendar.NextMonthStart(weekStart) <= now)
        {
            // A semana começou no mês anterior e o que ela daria já venceu (dia 1º no meio da semana).
            return 0;
        }

        var granted = 0;
        foreach (var subscription in await subscriptions.ListEarningWeeklyCreditsAsync(weekStart, ct))
        {
            // Uma transação por assinatura: a falha de uma não segura as outras, e a chave única impede a repetição.
            var added = await unitOfWork.InTransactionAsync(
                async () =>
                {
                    var bucket = CreditBucket.GrantPlanWeek(subscription, weekStart, now);
                    if (!await credits.TryAddKeyedGrantAsync(bucket, CreditLedgerEntry.ForGrant(bucket, now), ct))
                    {
                        return false;
                    }

                    audit.Record(
                        "system",
                        "credit.granted",
                        "credit_bucket",
                        bucket.Id,
                        new { source = "plan_week", delta = bucket.Granted, subscriptionId = subscription.Id });
                    return true;
                },
                ct);
            granted += added ? 1 : 0;
        }

        return granted;
    }

    public async Task<int> ExpirePlanCreditsAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var expired = 0;
        foreach (var bucket in await credits.ListExpiredWithBalanceAsync(now, ExpiredBucketsPerSweep, ct))
        {
            var done = await unitOfWork.InTransactionAsync(
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
                ct);
            expired += done ? 1 : 0;
        }

        return expired;
    }
}
