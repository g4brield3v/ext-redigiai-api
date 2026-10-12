using Microsoft.Extensions.Options;
using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Accounts;
using RedigiAI.Domain.Common;

namespace RedigiAI.Application.Accounts;

/// <summary>Quem é administrador (docs/07-propostas-de-redacao.md, A5). Vem só da configuração.</summary>
public sealed class AdminOptions
{
    public const string Section = "Admin";

    /// <summary>E-mails dos administradores, separados por vírgula.</summary>
    public string Emails { get; set; } = "";

    /// <summary>Exige o segundo fator na sessão para as rotas de administração.</summary>
    public bool Require2fa { get; set; } = true;

    public IReadOnlyList<string> EmailList =>
        Emails.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public bool IsListed(string email) =>
        EmailList.Contains(email.Trim(), StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Decide o papel de um usuário: administrador só com o e-mail principal na lista e verificado no provedor de
/// login. O e-mail do token não basta, porque o token não diz se ele foi verificado.
/// </summary>
internal sealed class AdminRole(IOptions<AdminOptions> options, IIdentityDirectory directory, ITestIdentities? testIdentities = null)
{
    public bool IsListed(string email) => options.Value.IsListed(email);

    public async Task<UserRole> ResolveAsync(User user, CancellationToken ct)
    {
        // A grande maioria é aluno: o provedor só é consultado para quem está na lista.
        if (!IsListed(user.Email))
        {
            return UserRole.Student;
        }

        var email = testIdentities?.FindPrimaryEmail(user.ClerkUserId)
            ?? await directory.FindPrimaryEmailAsync(user.ClerkUserId, ct);
        return email is { Verified: true } && IsListed(email.Address) ? UserRole.Admin : UserRole.Student;
    }
}

/// <summary>
/// Usuários de teste do ambiente de desenvolvimento, que não existem no provedor de login. Só é registrado com o
/// acesso de desenvolvimento; fora dele não há implementação e toda conta é consultada no provedor.
/// </summary>
public interface ITestIdentities
{
    PrimaryEmail? FindPrimaryEmail(string clerkUserId);
}

public interface IAdminAccessService
{
    /// <summary>
    /// Confere, no banco, que o usuário do token é administrador. Para os demais, <c>NOT_FOUND</c> (404): a área
    /// de administração não existe para eles. Administrador sem o segundo fator na sessão: <c>ADMIN_2FA_REQUIRED</c>.
    /// </summary>
    /// <returns>O id do administrador, para o registro de quem alterou o quê.</returns>
    Task<Guid> RequireAdminAsync(string clerkUserId, bool hasSecondFactor, CancellationToken ct);
}

internal sealed class AdminAccessService(IUserRepository users, IOptions<AdminOptions> options) : IAdminAccessService
{
    public async Task<Guid> RequireAdminAsync(string clerkUserId, bool hasSecondFactor, CancellationToken ct)
    {
        var user = await users.FindByClerkIdAsync(clerkUserId, ct);

        // O papel gravado vale junto com a lista atual: tirar o e-mail da configuração corta o acesso na hora.
        if (user is not { Role: UserRole.Admin } || !options.Value.IsListed(user.Email))
        {
            throw Errors.NotFound();
        }

        if (options.Value.Require2fa && !hasSecondFactor)
        {
            throw Errors.AdminSecondFactorRequired();
        }

        return user.Id;
    }
}
