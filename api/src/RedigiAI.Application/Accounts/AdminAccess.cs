using RedigiAI.Domain.Accounts;
using RedigiAI.Domain.Common;

namespace RedigiAI.Application.Accounts;

public interface IAdminAccessService
{
    /// <summary>
    /// Confere que o usuário do token é administrador: a flag <c>is_admin</c> no banco, lida a cada requisição (regra 16).
    /// Para os demais, <c>NOT_FOUND</c> (404): a área de administração não existe para eles.
    /// </summary>
    /// <returns>O id do administrador, para o registro de quem alterou o quê.</returns>
    Task<Guid> RequireAdminAsync(string clerkUserId, CancellationToken ct);
}

internal sealed class AdminAccessService(IUserRepository users) : IAdminAccessService
{
    public async Task<Guid> RequireAdminAsync(string clerkUserId, CancellationToken ct) =>
        await users.FindByClerkIdAsync(clerkUserId, ct) is { IsAdmin: true } user ? user.Id : throw Errors.NotFound();
}
