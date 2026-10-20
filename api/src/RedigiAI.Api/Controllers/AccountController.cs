using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RedigiAI.Api.Auth;
using RedigiAI.Api.Http;
using RedigiAI.Application.Accounts;
using RedigiAI.Domain.Common;

namespace RedigiAI.Api.Controllers;

public sealed record RegisterRequest(DateOnly? BirthDate, bool TermsAccepted);

public sealed record UpdateProfileRequest(string? Name, UpdateEmailPreferences? EmailPreferences);

/// <summary>Conta do aluno logado. Tudo aqui usa só o usuário do token.</summary>
[ApiController]
[Authorize]
[Route("v1/me")]
[Tags("Account")]
[Produces("application/json")]
[ProducesResponseType<ErrorResponse>(StatusCodes.Status401Unauthorized)]
public sealed class AccountController(IAccountService accounts, ILogger<AccountController> logger) : ControllerBase
{
    /// <summary>
    /// Primeiro acesso: cria o usuário a partir do token, grava a data de nascimento (recusa menores de 16)
    /// e concede o crédito grátis uma única vez. Repetido, devolve o perfil que já existe.
    /// </summary>
    [HttpPost(Name = "registerMe")]
    [ProducesResponseType<ProfileDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProfileDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request, CancellationToken ct)
    {
        var identity = User.GetClerkIdentity();
        if (string.IsNullOrWhiteSpace(identity.Email))
        {
            // Configuração do Clerk: o token de sessão precisa trazer a claim "email".
            logger.LogError("Clerk session token has no 'email' claim; customize the session token in Clerk");
            throw Errors.Internal();
        }

        var result = await accounts.RegisterAsync(
            new RegisterUserCommand(
                identity.UserId,
                identity.Email,
                identity.Name,
                request.BirthDate,
                request.TermsAccepted,
                HttpContext.GetRequestOrigin()),
            ct);

        return result.Created ? Created("/v1/me", result.Profile) : Ok(result.Profile);
    }

    /// <summary>Perfil, saldo de correções por origem e preferências de e-mail.</summary>
    [HttpGet(Name = "getMe")]
    [ProducesResponseType<ProfileDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(CancellationToken ct) =>
        Ok(await accounts.GetProfileAsync(User.GetClerkIdentity().UserId, HttpContext.GetRequestOrigin(), ct));

    /// <summary>
    /// Aceita a versão vigente dos Termos de uso e da Política de privacidade. Enquanto o perfil traz
    /// <c>termsAcceptanceRequired</c>, o envio de redação responde <c>TERMS_ACCEPTANCE_REQUIRED</c> (409).
    /// </summary>
    [HttpPost("terms", Name = "acceptTerms")]
    [ProducesResponseType<ProfileDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AcceptTerms(CancellationToken ct) =>
        Ok(await accounts.AcceptTermsAsync(User.GetClerkIdentity().UserId, HttpContext.GetRequestOrigin(), ct));

    /// <summary>Altera o nome e as preferências de e-mail.</summary>
    [HttpPatch(Name = "updateMe")]
    [ProducesResponseType<ProfileDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Update([FromBody] UpdateProfileRequest request, CancellationToken ct) =>
        Ok(await accounts.UpdateProfileAsync(
            new UpdateProfileCommand(User.GetClerkIdentity().UserId, request.Name, request.EmailPreferences), ct));
}
