namespace RedigiAI.Domain.Billing;

public enum CreditSource
{
    Trial,
    PlanWeek,
    Single,
    Refund,
}

/// <summary>Cache do saldo de uma concessão. O débito é feito por UPDATE condicional no repositório.</summary>
public sealed class CreditBucket
{
    public const int TrialCredits = 1;

    private CreditBucket() { }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public CreditSource Source { get; private set; }
    public int Granted { get; private set; }
    public int Remaining { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Identifica uma concessão que não pode se repetir (a semana de uma assinatura); único no banco.</summary>
    public string? GrantKey { get; private set; }

    /// <summary>
    /// Ordem de consumo (regra 15): primeiro o plano, que vence antes, depois o teste grátis e por fim os avulsos.
    /// </summary>
    public static IReadOnlyList<CreditBucket> InConsumptionOrder(IEnumerable<CreditBucket> buckets) =>
        [.. buckets
            .OrderBy(b => b.Source switch
            {
                CreditSource.PlanWeek => 0,
                CreditSource.Trial => 1,
                _ => 2,
            })
            .ThenBy(b => b.ExpiresAt ?? DateTimeOffset.MaxValue)
            .ThenBy(b => b.CreatedAt)];

    public static CreditBucket Grant(Guid userId, CreditSource source, int amount, DateTimeOffset? expiresAt, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(amount, 1);
        return new CreditBucket
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Source = source,
            Granted = amount,
            Remaining = amount,
            ExpiresAt = expiresAt,
            CreatedAt = now,
        };
    }

    /// <summary>
    /// As correções da semana de uma assinatura, que vencem no dia 1º seguinte à segunda-feira da semana.
    /// </summary>
    public static CreditBucket GrantPlanWeek(Subscription subscription, DateTimeOffset weekStart, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            UserId = subscription.UserId,
            Source = CreditSource.PlanWeek,
            Granted = PlanCalendar.WeeklyCredits,
            Remaining = PlanCalendar.WeeklyCredits,
            ExpiresAt = PlanCalendar.NextMonthStart(weekStart),
            CreatedAt = now,
            GrantKey = subscription.WeeklyGrantKey(weekStart),
        };

    /// <summary>Teste grátis: 1 correção por conta, sem vencimento.</summary>
    public static CreditBucket GrantTrial(Guid userId, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Source = CreditSource.Trial,
            Granted = TrialCredits,
            Remaining = TrialCredits,
            ExpiresAt = null,
            CreatedAt = now,
        };
}
