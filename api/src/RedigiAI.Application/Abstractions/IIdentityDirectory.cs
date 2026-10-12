namespace RedigiAI.Application.Abstractions;

/// <summary>O e-mail principal da conta no provedor de login e se ele foi verificado.</summary>
public sealed record PrimaryEmail(string Address, bool Verified);

/// <summary>Consulta de contas no provedor de login (Backend API do Clerk).</summary>
public interface IIdentityDirectory
{
    /// <summary>
    /// O e-mail principal da conta. <c>null</c> quando a conta não existe, não tem e-mail principal ou a consulta
    /// falhou: quem chama trata a ausência como "não confirmado".
    /// </summary>
    Task<PrimaryEmail?> FindPrimaryEmailAsync(string clerkUserId, CancellationToken ct);
}
