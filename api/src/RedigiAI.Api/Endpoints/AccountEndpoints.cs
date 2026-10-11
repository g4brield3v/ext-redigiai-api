using System.Security.Claims;
using RedigiAI.Api.Auth;
using RedigiAI.Application.Accounts;
using RedigiAI.Domain.Common;

namespace RedigiAI.Api.Endpoints;

public sealed record RegisterRequest(DateOnly? BirthDate, bool TermsAccepted);

public sealed record UpdateProfileRequest(string? Name, UpdateEmailPreferences? EmailPreferences);

internal static class AccountEndpoints
{
    public static void MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/v1/me").WithTags("Account").RequireAuthorization();

        me.MapPost("", async (
                RegisterRequest request,
                ClaimsPrincipal principal,
                HttpContext http,
                RegisterUserHandler handler,
                ILoggerFactory loggers,
                CancellationToken ct) =>
            {
                var identity = principal.GetClerkIdentity();
                if (string.IsNullOrWhiteSpace(identity.Email))
                {
                    // Configuração do Clerk: o token de sessão precisa trazer a claim "email".
                    loggers.CreateLogger("RedigiAI.Api.Account")
                        .LogError("Clerk session token has no 'email' claim; customize the session token in Clerk");
                    throw Errors.Internal();
                }

                var result = await handler.HandleAsync(
                    new RegisterUserCommand(
                        identity.UserId,
                        identity.Email,
                        identity.Name,
                        request.BirthDate,
                        request.TermsAccepted,
                        http.GetRequestOrigin()),
                    ct);

                return result.Created
                    ? Results.Created("/v1/me", result.Profile)
                    : Results.Ok(result.Profile);
            })
            .WithName("registerMe")
            .Produces<ProfileDto>(200)
            .Produces<ProfileDto>(201)
            .ProducesError(401, 422);

        me.MapGet("", async (ClaimsPrincipal principal, HttpContext http, GetProfileHandler handler, CancellationToken ct) =>
                TypedResults.Ok(await handler.HandleAsync(principal.GetClerkIdentity().UserId, http.GetRequestOrigin(), ct)))
            .WithName("getMe")
            .ProducesError(401, 404);

        me.MapPatch("", async (
                UpdateProfileRequest request, ClaimsPrincipal principal, UpdateProfileHandler handler, CancellationToken ct) =>
                TypedResults.Ok(await handler.HandleAsync(
                    new UpdateProfileCommand(principal.GetClerkIdentity().UserId, request.Name, request.EmailPreferences),
                    ct)))
            .WithName("updateMe")
            .ProducesError(401, 404, 422);
    }
}
