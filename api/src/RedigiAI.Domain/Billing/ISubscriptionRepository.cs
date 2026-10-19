namespace RedigiAI.Domain.Billing;

public interface ISubscriptionRepository
{
    void Add(Subscription subscription);

    /// <summary>Assinaturas que recebem as correções da semana que começa em <paramref name="weekStart"/>.</summary>
    Task<IReadOnlyList<Subscription>> ListEarningWeeklyCreditsAsync(DateTimeOffset weekStart, CancellationToken ct);

    /// <summary>A assinatura mais recente do aluno, em qualquer situação; <c>null</c> se ele nunca assinou.</summary>
    Task<Subscription?> FindLatestAsync(Guid userId, CancellationToken ct);
}
