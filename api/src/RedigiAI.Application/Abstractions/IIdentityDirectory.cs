namespace RedigiAI.Application.Abstractions;

/// <summary>O e-mail principal da conta no provedor de login e se ele foi verificado.</summary>
public sealed record PrimaryEmail(string Address, bool Verified);

/// <summary>O provedor de login não pôde ser consultado agora.</summary>
public sealed class IdentityDirectoryUnavailableException(string reason) : Exception(reason);

/// <summary>Consulta de contas no provedor de login (Backend API do Clerk).</summary>
public interface IIdentityDirectory
{
    /// <summary>O e-mail principal da conta. <c>null</c> quando a conta não existe ou não tem e-mail principal.</summary>
    /// <exception cref="IdentityDirectoryUnavailableException">
    /// Não deu para consultar (provedor fora do ar, limite de chamadas, chave ausente). Não é o mesmo que "não
    /// verificado": quem chama não muda o papel de ninguém com base nisso.
    /// </exception>
    Task<PrimaryEmail?> FindPrimaryEmailAsync(string clerkUserId, CancellationToken ct);
}
