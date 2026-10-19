namespace RedigiAI.Domain.Billing;

public interface ISubscriptionRepository
{
    void Add(Subscription subscription);

    /// <summary>A assinatura mais recente do aluno, em qualquer situação; <c>null</c> se ele nunca assinou.</summary>
    Task<Subscription?> FindLatestAsync(Guid userId, CancellationToken ct);
}
