namespace RedigiAI.Domain.Billing;

public interface ISubscriptionRepository
{
    void Add(Subscription subscription);

    /// <summary>
    /// Assinaturas que ainda não receberam as correções da semana que começa em <paramref name="weekStart"/> e têm
    /// direito a elas (ativas, de antes da segunda-feira, de contas não excluídas).
    /// </summary>
    Task<IReadOnlyList<Subscription>> ListAwaitingWeeklyCreditsAsync(DateTimeOffset weekStart, int limit, CancellationToken ct);

    /// <summary>A assinatura mais recente do aluno, em qualquer situação; <c>null</c> se ele nunca assinou.</summary>
    Task<Subscription?> FindLatestAsync(Guid userId, CancellationToken ct);
}
