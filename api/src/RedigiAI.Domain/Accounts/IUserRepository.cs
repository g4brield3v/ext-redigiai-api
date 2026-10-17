namespace RedigiAI.Domain.Accounts;

public interface IUserRepository
{
    /// <summary>Usuário ativo (não excluído) da conta do Clerk.</summary>
    Task<User?> FindByClerkIdAsync(string clerkUserId, CancellationToken ct);

    /// <summary>Usuário ativo (não excluído) pelo id; para os jobs, que agem sem um aluno logado.</summary>
    Task<User?> FindActiveByIdAsync(Guid userId, CancellationToken ct);

    void Add(User user);

    /// <summary>Atualiza <c>last_active_at</c> no máximo uma vez por hora.</summary>
    Task TouchAsync(Guid userId, DateTimeOffset now, CancellationToken ct);
}
