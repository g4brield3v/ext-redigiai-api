namespace RedigiAI.Domain.Accounts;

public interface IUserRepository
{
    /// <summary>Usuário ativo (não excluído) da conta do Clerk.</summary>
    Task<User?> FindByClerkIdAsync(string clerkUserId, CancellationToken ct);

    void Add(User user);

    /// <summary>Atualiza <c>last_active_at</c> no máximo uma vez por hora.</summary>
    Task TouchAsync(Guid userId, DateTimeOffset now, CancellationToken ct);
}
