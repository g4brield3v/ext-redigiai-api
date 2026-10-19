namespace RedigiAI.Domain.Billing;

public enum CreditSource
{
    Trial,
    Plan,
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
    /// Quando a compra deste bucket foi estornada ou contestada: o saldo saiu e nenhuma correção volta para ele, nem a
    /// que estava reservada para uma redação em andamento.
    /// </summary>
    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary>
    /// Ordem de consumo (regra 15): primeiro o plano, que vence antes, depois o teste grátis e por fim os avulsos.
    /// </summary>
    public static IReadOnlyList<CreditBucket> InConsumptionOrder(IEnumerable<CreditBucket> buckets) =>
        [.. buckets
            .OrderBy(b => b.Source switch
            {
                CreditSource.Plan => 0,
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

    /// <summary>As correções avulsas de uma compra aprovada: sem vencimento, uma concessão por compra.</summary>
    public static CreditBucket GrantForPayment(Payment payment, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            UserId = payment.UserId,
            Source = CreditSource.Single,
            Granted = payment.Quantity,
            Remaining = payment.Quantity,
            ExpiresAt = null,
            CreatedAt = now,
            GrantKey = PaymentGrantKey(payment.Id),
        };

    /// <summary>
    /// As 12 correções de uma cobrança do plano, que valem até a cobrança seguinte (<paramref name="cycleEnd"/>).
    /// </summary>
    public static CreditBucket GrantPlanCycle(Payment payment, DateTimeOffset cycleEnd, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            UserId = payment.UserId,
            Source = CreditSource.Plan,
            Granted = Subscription.CreditsPerCycle,
            Remaining = Subscription.CreditsPerCycle,
            ExpiresAt = cycleEnd,
            CreatedAt = now,
            GrantKey = PaymentGrantKey(payment.Id),
        };

    /// <summary>Chave da concessão de uma compra: acha o bucket dela no estorno.</summary>
    public static string PaymentGrantKey(Guid paymentId) => $"payment:{paymentId}";

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
