using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using RedigiAI.Api.Http;
using RedigiAI.Application.Abstractions;
using RedigiAI.Application.Accounts;
using RedigiAI.Application.Catalog;
using RedigiAI.Domain.Common;

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

/// <summary>
/// Usuário de teste. Sem <c>Age</c>, a conta fica sem cadastro, para testar o primeiro acesso. Com <c>IsAdmin</c>, a
/// flag de administrador é ligada na conta (só aqui, no desenvolvimento).
/// </summary>
internal sealed record DevUser(
    string Key, string ClerkUserId, string Email, string Name, int? Age, int ExtraCredits, bool IsAdmin = false);

/// <summary>Os usuários de teste: três alunos e o administrador. Só é registrado com o acesso de desenvolvimento.</summary>
internal sealed class DevUsers
{
    public IReadOnlyList<DevUser> All { get; } =
    [
        new("ana", "dev_ana", "ana.souza@example.com", "Ana Souza", 26, 5),
        new("bruno", "dev_bruno", "bruno.lima@example.com", "Bruno Lima", 16, 0),
        new("nova", "dev_nova", "conta.nova@example.com", "Conta Nova", null, 0),
        new("admin", "dev_admin", "admin@example.com", "Administração", 30, 0, IsAdmin: true),
    ];
}

internal sealed class DevAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, DevUsers users)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var key = Request.Headers[DevAuthentication.Header].ToString();
        var user = users.All.FirstOrDefault(candidate => candidate.Key == key);
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
/// Deixa o banco local pronto ao subir a API em modo de desenvolvimento: migrações, catálogo e os usuários
/// de teste já cadastrados (a "Conta Nova" fica de fora, para testar o primeiro acesso).
/// </summary>
internal sealed class DevEnvironmentSetup(IServiceScopeFactory scopes, ILogger<DevEnvironmentSetup> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;

        await services.GetRequiredService<IDatabaseAdmin>().MigrateAsync(ct);
        await services.GetRequiredService<ICatalogSeedService>().SeedAsync(ct);

        var accounts = services.GetRequiredService<ITestAccountService>();
        foreach (var user in services.GetRequiredService<DevUsers>().All)
        {
            if (user.Age is { } age)
            {
                await accounts.EnsureAsync(new TestAccount(user.ClerkUserId, user.Email, user.Name, age, user.ExtraCredits, user.IsAdmin), ct);
            }
        }

        logger.LogWarning("Development access is ON: requests with the X-Dev-User header are accepted without login");
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
