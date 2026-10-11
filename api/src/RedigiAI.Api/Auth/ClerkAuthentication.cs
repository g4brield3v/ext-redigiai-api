using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using RedigiAI.Api.Http;
using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Common;
using RedigiAI.Infrastructure.Clerk;

namespace RedigiAI.Api.Auth;

internal sealed record ClerkIdentity(string UserId, string? Email, string? Name, string? SessionId);

internal static class ClerkAuthentication
{
    /// <summary>
    /// Valida o JWT de sessão do Clerk: assinatura (JWKS), emissor, expiração e audiência.
    /// O token de sessão do Clerk não traz <c>aud</c> por padrão; a parte autorizada (<c>azp</c>) é conferida
    /// contra as origens permitidas e <c>aud</c> é exigido quando <c>Clerk__Audience</c> está configurado.
    /// </summary>
    public static IServiceCollection AddClerkAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ClerkOptions>(configuration.GetSection(ClerkOptions.Section));
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddAuthorization();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<ClerkOptions>, IConfiguration>((jwt, clerkOptions, config) =>
            {
                var clerk = clerkOptions.Value;
                var allowedOrigins = CorsOrigins.From(config);

                jwt.MapInboundClaims = false;
                if (!string.IsNullOrWhiteSpace(clerk.JwksUrl))
                {
                    jwt.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                        clerk.JwksUrl,
                        new JwksRetriever(clerk.Issuer),
                        new HttpDocumentRetriever { RequireHttps = true });
                }

                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = clerk.Issuer,
                    ValidateAudience = !string.IsNullOrWhiteSpace(clerk.Audience),
                    ValidAudience = clerk.Audience,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "sub",
                };

                jwt.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        var principal = context.Principal!;
                        var authorizedParty = principal.FindFirstValue("azp");
                        if (string.IsNullOrEmpty(principal.FindFirstValue("sub")))
                        {
                            context.Fail("Token without subject.");
                        }
                        else if (authorizedParty is not null && !allowedOrigins.Contains(authorizedParty))
                        {
                            context.Fail("Token issued for another origin.");
                        }

                        return Task.CompletedTask;
                    },
                    OnChallenge = async context =>
                    {
                        context.HandleResponse();
                        if (!context.Response.HasStarted)
                        {
                            await ErrorWriter.WriteAsync(context.HttpContext, Errors.Unauthenticated());
                        }
                    },
                };
            });

        return services;
    }

    public static ClerkIdentity GetClerkIdentity(this ClaimsPrincipal principal) =>
        new(
            principal.FindFirstValue("sub") ?? throw Errors.Unauthenticated(),
            principal.FindFirstValue("email"),
            principal.FindFirstValue("name"),
            principal.FindFirstValue("sid"));

    public static RequestOrigin GetRequestOrigin(this HttpContext context) =>
        new(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            context.Request.Headers.UserAgent.ToString() is { Length: > 0 } userAgent ? userAgent : null,
            context.User.FindFirstValue("sid"));

    /// <summary>O Clerk publica só o JWKS; o emissor vem da configuração.</summary>
    private sealed class JwksRetriever(string issuer) : IConfigurationRetriever<OpenIdConnectConfiguration>
    {
        public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(
            string address, IDocumentRetriever retriever, CancellationToken cancel)
        {
            var json = await retriever.GetDocumentAsync(address, cancel);
            var configuration = new OpenIdConnectConfiguration { Issuer = issuer };
            foreach (var key in new JsonWebKeySet(json).GetSigningKeys())
            {
                configuration.SigningKeys.Add(key);
            }

            return configuration;
        }
    }
}

internal static class CorsOrigins
{
    public static HashSet<string> From(IConfiguration configuration) =>
        (configuration["Cors:AllowedOrigins"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(origin => origin.TrimEnd('/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
