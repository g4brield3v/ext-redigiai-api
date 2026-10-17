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
internal sealed class AdminRole(
    IOptions<AdminOptions> options,
    IIdentityDirectory directory,
    IAuditLog audit,
    IUnitOfWork unitOfWork,
    ITestIdentities? testIdentities = null)
{
    public bool IsListed(string email) => options.Value.IsListed(email);

    /// <returns>O papel; <c>null</c> quando o provedor não pôde ser consultado e não dá para saber.</returns>
    public async Task<UserRole?> ResolveAsync(User user, CancellationToken ct)
    {
        // A grande maioria é aluno: o provedor só é consultado para quem está na lista.
        if (!IsListed(user.Email))
        {
            return UserRole.Student;
        }

        try
        {
            var email = testIdentities?.FindPrimaryEmail(user.ClerkUserId)
                ?? await directory.FindPrimaryEmailAsync(user.ClerkUserId, ct);
            return email is { Verified: true } && IsListed(email.Address) ? UserRole.Admin : UserRole.Student;
        }
        catch (IdentityDirectoryUnavailableException)
        {
            return null;
        }
    }

    /// <summary>
    /// Confere o papel uma vez por sessão de login e grava o resultado. Só quem está na lista de administradores, ou
    /// ainda tem o papel gravado, passa pela consulta: para os demais alunos nada é lido nem gravado.
    /// </summary>
    /// <returns>
    /// <c>false</c> quando a conferência era devida e o provedor não respondeu: o papel fica como estava e a sessão
    /// continua por conferir.
    /// </returns>
    public async Task<bool> EnsureCheckedAsync(User user, string? sessionId, CancellationToken ct)
    {
        if ((user.Role != UserRole.Admin && !IsListed(user.Email)) || !user.NeedsRoleCheck(sessionId))
        {
            return true;
        }

        if (await ResolveAsync(user, ct) is not { } role)
        {
            return false;
        }

        if (user.ConfirmRole(role, sessionId))
        {
            audit.Record("system", "user.role_changed", "user", user.Id, new { role = role == UserRole.Admin ? "admin" : "student" });
        }

        await unitOfWork.SaveChangesAsync(ct);
        return true;
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
    /// Confere que o usuário do token é administrador: o papel gravado, a lista atual e, numa sessão em que o papel
    /// ainda não foi conferido, o provedor de login. Para os demais, <c>NOT_FOUND</c> (404): a área de administração
    /// não existe para eles. Administrador sem o segundo fator na sessão: <c>ADMIN_2FA_REQUIRED</c>.
    /// </summary>
    /// <param name="sessionId">A sessão de login do token.</param>
    /// <returns>O id do administrador, para o registro de quem alterou o quê.</returns>
    Task<Guid> RequireAdminAsync(string clerkUserId, string? sessionId, bool hasSecondFactor, CancellationToken ct);
}

internal sealed class AdminAccessService(IUserRepository users, AdminRole adminRole, IOptions<AdminOptions> options) : IAdminAccessService
{
    public async Task<Guid> RequireAdminAsync(string clerkUserId, string? sessionId, bool hasSecondFactor, CancellationToken ct)
    {
        var user = await users.FindByClerkIdAsync(clerkUserId, ct) ?? throw Errors.NotFound();

        // Sessão nova que chega direto às rotas de administração: o papel é conferido aqui, sem depender de o
        // navegador ter lido o perfil antes. Sem resposta do provedor, a porta fica fechada.
        var roleIsCurrent = await adminRole.EnsureCheckedAsync(user, sessionId, ct);

        // O papel gravado vale junto com a lista atual: tirar o e-mail da configuração corta o acesso na hora.
        if (!roleIsCurrent || user.Role != UserRole.Admin || !options.Value.IsListed(user.Email))
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
