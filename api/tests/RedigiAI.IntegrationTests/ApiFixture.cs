using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using RedigiAI.Application.Catalog;
using RedigiAI.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace RedigiAI.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
    public const string Name = "api";
}

/// <summary>API real sobre um PostgreSQL real (Testcontainers), com tokens assinados por uma chave de teste.</summary>
public sealed class ApiFixture : IAsyncLifetime
{
    public const string Issuer = "https://clerk.test";
    public const string WebOrigin = "http://localhost:5173";

    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private readonly RsaSecurityKey _signingKey = new(RSA.Create(2048)) { KeyId = "test-key" };

    public byte[] WebhookKey { get; } = RandomNumberGenerator.GetBytes(24);

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _database.StartAsync();
        Factory = CreateFactory();

        await using var scope = Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<ICatalogSeedService>().SeedAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _database.DisposeAsync();
    }

    public WebApplicationFactory<Program> CreateFactory(Action<IWebHostBuilder>? configure = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Default", _database.GetConnectionString());
            builder.UseSetting("Clerk:Issuer", Issuer);
            builder.UseSetting("Clerk:JwksUrl", "");
            builder.UseSetting("Clerk:Audience", "");
            builder.UseSetting("Clerk:WebhookSecret", "whsec_" + Convert.ToBase64String(WebhookKey));
            builder.UseSetting("Cors:AllowedOrigins", WebOrigin);
            builder.UseSetting("Legal:TermsVersion", "1");
            builder.UseSetting("RateLimit:PerUserPerMinute", "10000");
            builder.UseSetting("RateLimit:PerIpPerMinute", "10000");
            builder.UseSetting("Proxy:TrustForwardedHeaders", "false");
            configure?.Invoke(builder);

            builder.ConfigureTestServices(services =>
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, jwt =>
                {
                    var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                    configuration.SigningKeys.Add(_signingKey);
                    jwt.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                }));
        });

    public HttpClient CreateClient(string? token = null) => CreateClient(Factory, token);

    public static HttpClient CreateClient(WebApplicationFactory<Program> factory, string? token = null)
    {
        var client = factory.CreateClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    public static string NewClerkUserId() => $"user_{Guid.NewGuid():N}";

    public string CreateToken(
        string clerkUserId,
        string? email = null,
        string? name = "Ana Souza",
        string? sessionId = null,
        string issuer = Issuer,
        string? authorizedParty = WebOrigin,
        DateTime? expires = null,
        SecurityKey? signingKey = null)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = clerkUserId,
            ["email"] = email ?? $"{clerkUserId}@example.com",
            ["sid"] = sessionId ?? $"sess_{Guid.NewGuid():N}",
        };
        if (name is not null)
        {
            claims["name"] = name;
        }

        if (authorizedParty is not null)
        {
            claims["azp"] = authorizedParty;
        }

        var expiration = expires ?? DateTime.UtcNow.AddMinutes(5);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Claims = claims,
            NotBefore = expiration.AddMinutes(-10),
            IssuedAt = expiration.AddMinutes(-10),
            Expires = expiration,
            SigningCredentials = new SigningCredentials(signingKey ?? _signingKey, SecurityAlgorithms.RsaSha256),
        });
    }

    public HttpRequestMessage CreateClerkWebhook(string json, byte[]? key = null, string? signedJson = null)
    {
        var id = $"msg_{Guid.NewGuid():N}";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signed = Encoding.UTF8.GetBytes($"{id}.{timestamp}.{signedJson ?? json}");
        var signature = Convert.ToBase64String(HMACSHA256.HashData(key ?? WebhookKey, signed));

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/webhooks/clerk")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("svix-id", id);
        request.Headers.Add("svix-timestamp", timestamp);
        request.Headers.Add("svix-signature", $"v1,{signature}");
        return request;
    }

    public async Task<T> QueryAsync<T>(Func<AppDbContext, Task<T>> query)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
}
