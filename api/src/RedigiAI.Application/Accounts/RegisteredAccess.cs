using RedigiAI.Domain.Accounts;
using RedigiAI.Domain.Common;

namespace RedigiAI.Application.Accounts;

public interface IRegisteredAccess
{
    /// <summary>
    /// Confere que o usuário do token tem conta na plataforma (cadastro concluído, com idade mínima), e não só sessão
    /// no Clerk. Sem conta: <c>PROFILE_NOT_FOUND</c>.
    /// </summary>
    Task RequireRegisteredAsync(string clerkUserId, CancellationToken ct);
}

internal sealed class RegisteredAccess(IUserRepository users) : IRegisteredAccess
{
    public async Task RequireRegisteredAsync(string clerkUserId, CancellationToken ct)
    {
        if (await users.FindByClerkIdAsync(clerkUserId, ct) is null)
        {
            throw Errors.ProfileNotFound();
        }
    }
}
