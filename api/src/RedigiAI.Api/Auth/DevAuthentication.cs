using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RedigiAI.Api.Http;
using RedigiAI.Application.Abstractions;
using RedigiAI.Application.Accounts;
using RedigiAI.Domain.Billing;
using RedigiAI.Domain.Common;
using RedigiAI.Infrastructure.Persistence;

namespace RedigiAI.Api.Auth;

/// <summary>
/// Acesso sem login, só para rodar o sistema na máquina de quem desenvolve: o front manda o cabeçalho
/// <c>X-Dev-User</c> com um dos alunos de teste e a API o trata como autenticado.
/// Só existe com o ambiente <c>Development</c> e <c>DevAuth__Enabled=true</c>; fora disso a API nem sobe.
/// </summary>
internal static class DevAuthentication
{
    public const string Scheme = "Dev";
    public const string SmartScheme = "DevOrClerk";
    public const string Header = "X-Dev-User";

    public static bool IsEnabled(IConfiguration configuration, IHostEnvironment environment)
    {
        if (!configuration.GetValue<bool>("DevAuth:Enabled"))
        {
            return false;
        }

        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "DevAuth__Enabled=true só é aceito no ambiente Development. Remova a variável para subir a API.");
        }

        return true;
    }
}

/// <summary>Aluno de teste. Sem <c>Age</c>, a conta fica sem cadastro, para testar o primeiro acesso.</summary>
internal sealed record DevUser(string Key, string ClerkUserId, string Email, string Name, int? Age, int ExtraCredits)
{
    public static readonly DevUser[] All =
    [
        new("ana", "dev_ana", "ana.souza@example.com", "Ana Souza", 26, 5),
        new("bruno", "dev_bruno", "bruno.lima@example.com", "Bruno Lima", 16, 0),
        new("nova", "dev_nova", "conta.nova@example.com", "Conta Nova", null, 0),
    ];
}

internal sealed class DevAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var key = Request.Headers[DevAuthentication.Header].ToString();
        var user = DevUser.All.FirstOrDefault(candidate => candidate.Key == key);
        if (user is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("Unknown development user."));
        }

        var identity = new ClaimsIdentity(
            [
                new Claim("sub", user.ClerkUserId),
                new Claim("email", user.Email),
                new Claim("name", user.Name),
                new Claim("sid", $"dev-{user.Key}"),
            ],
            DevAuthentication.Scheme,
            nameType: "sub",
            roleType: null);

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), DevAuthentication.Scheme)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        ErrorWriter.WriteAsync(Context, Errors.Unauthenticated());
}

/// <summary>
/// Deixa o banco local pronto ao subir a API em modo de desenvolvimento: migrações, catálogo e os alunos
/// de teste já cadastrados (a "Conta Nova" fica de fora, para testar o primeiro acesso).
/// </summary>
internal sealed class DevEnvironmentSetup(IServiceScopeFactory scopes, TimeProvider time, ILogger<DevEnvironmentSetup> logger)
    : IHostedService
{
    private static readonly RequestOrigin Origin = new("127.0.0.1", "dev-setup", null);

    public async Task StartAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AppDbContext>();

        await db.Database.MigrateAsync(ct);
        await services.GetRequiredService<CatalogSeeder>().SeedAsync(ct);

        var today = BrasiliaTime.DateOf(time.GetUtcNow());
        foreach (var user in DevUser.All)
        {
            if (user.Age is not { } age)
            {
                continue;
            }

            // Cem dias depois do aniversário: a idade não muda de um dia para o outro durante os testes.
            var birthDate = today.AddYears(-age).AddDays(-100);
            var result = await services.GetRequiredService<RegisterUserHandler>().HandleAsync(
                new RegisterUserCommand(user.ClerkUserId, user.Email, user.Name, birthDate, TermsAccepted: true, Origin), ct);

            if (result.Created && user.ExtraCredits > 0)
            {
                var now = time.GetUtcNow();
                var bucket = CreditBucket.Grant(result.Profile.Id, CreditSource.Single, user.ExtraCredits, expiresAt: null, now);
                services.GetRequiredService<ICreditRepository>().AddGrant(bucket, CreditLedgerEntry.ForGrant(bucket, now));
                services.GetRequiredService<IAuditLog>().Record(
                    "dev-setup", "credit.granted", "credit_bucket", bucket.Id, new { source = "single", delta = bucket.Granted });
                await services.GetRequiredService<IUnitOfWork>().SaveChangesAsync(ct);
            }
        }

        logger.LogWarning("Development access is ON: requests with the X-Dev-User header are accepted without login");
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
