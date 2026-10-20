using RedigiAI.Application.Abstractions;

namespace RedigiAI.Application.Accounts;

public sealed record RegisterUserCommand(
    string ClerkUserId,
    string Email,
    string? Name,
    DateOnly? BirthDate,
    bool TermsAccepted,
    RequestOrigin Origin);

public sealed record RegisterUserResult(ProfileDto Profile, bool Created);

public sealed record UpdateEmailPreferences(bool? CorrectionReady, bool? IllegibleReminder, bool? NewThemes);

/// <param name="Name">Ausente = não muda; vazio = apaga o nome.</param>
public sealed record UpdateProfileCommand(string ClerkUserId, string? Name, UpdateEmailPreferences? EmailPreferences);

/// <summary>Conta do aluno: primeiro acesso, perfil e preferências.</summary>
public interface IAccountService
{
    /// <summary>
    /// Primeiro acesso: cria o usuário local e concede o crédito grátis uma única vez.
    /// Repetido ou simultâneo, devolve o usuário que já existe.
    /// </summary>
    Task<RegisterUserResult> RegisterAsync(RegisterUserCommand command, CancellationToken ct);

    Task<ProfileDto> GetProfileAsync(string clerkUserId, RequestOrigin origin, CancellationToken ct);

    Task<ProfileDto> UpdateProfileAsync(UpdateProfileCommand command, CancellationToken ct);

    /// <summary>Grava o aceite da versão vigente dos Termos e da Política (quando o perfil traz <c>termsAcceptanceRequired</c>).</summary>
    Task<ProfileDto> AcceptTermsAsync(string clerkUserId, RequestOrigin origin, CancellationToken ct);

    /// <summary>Reflete no usuário local o e-mail alterado no Clerk.</summary>
    Task SyncEmailAsync(string clerkUserId, string email, CancellationToken ct);
}
