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
