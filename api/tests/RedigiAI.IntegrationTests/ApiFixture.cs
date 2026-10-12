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
using System.Collections.Concurrent;
using RedigiAI.Application.Abstractions;
using RedigiAI.Application.Catalog;
using RedigiAI.Domain.Common;
using RedigiAI.Domain.Essays;
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
    public const string AdminEmail = "admin@redigiai.test";

    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private readonly RsaSecurityKey _signingKey = new(RSA.Create(2048)) { KeyId = "test-key" };

    public byte[] WebhookKey { get; } = RandomNumberGenerator.GetBytes(24);

    /// <summary>Faz o papel do Cloudinary: o que o teste "enviou" e a assinatura devolvida ao navegador.</summary>
    public FakePhotoStorage Photos { get; } = new();

    /// <summary>Faz o papel do Claude na transcrição: devolve o que o teste roteirizar.</summary>
    public FakeTranscriber Transcriber { get; } = new();

    /// <summary>Faz o papel do Claude na correção: devolve o JSON que o teste roteirizar.</summary>
    public FakeGrader Grader { get; } = new();

    /// <summary>Faz o papel da Backend API do Clerk: o e-mail principal de cada conta e se está verificado.</summary>
    public FakeIdentityDirectory Directory { get; } = new();

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
            builder.UseSetting("Admin:Emails", AdminEmail);
            builder.UseSetting("Admin:Require2fa", "true");
            builder.UseSetting("RateLimit:PerUserPerMinute", "10000");
            builder.UseSetting("RateLimit:PerIpPerMinute", "10000");
            builder.UseSetting("RateLimit:EssayBatchPerHour", "10000");
            builder.UseSetting("Proxy:TrustForwardedHeaders", "false");
            // O .env da máquina de desenvolvimento pode ligar o acesso sem login; nos testes ele fica desligado.
            builder.UseSetting("DevAuth:Enabled", "false");
            configure?.Invoke(builder);

            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IPhotoStorage>(Photos);
                services.AddSingleton<IEssayTranscriber>(Transcriber);
                services.AddSingleton<IEssayGrader>(Grader);
                services.AddSingleton<IIdentityDirectory>(Directory);
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, jwt =>
                {
                    var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                    configuration.SigningKeys.Add(_signingKey);
                    jwt.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                });
            });
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
        SecurityKey? signingKey = null,
        bool secondFactor = false)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = clerkUserId,
            ["email"] = email ?? $"{clerkUserId}@example.com",
            ["sid"] = sessionId ?? $"sess_{Guid.NewGuid():N}",
        };
        // Como no token do Clerk: minutos desde o primeiro e desde o segundo fator (-1 = sem segundo fator).
        claims["fva"] = new[] { 3, secondFactor ? 2 : -1 };
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

public sealed class FakeIdentityDirectory : IIdentityDirectory
{
    /// <summary>Contas conhecidas; as demais não existem no provedor.</summary>
    public ConcurrentDictionary<string, PrimaryEmail> Accounts { get; } = new();

    public ConcurrentQueue<string> Lookups { get; } = new();

    public Task<PrimaryEmail?> FindPrimaryEmailAsync(string clerkUserId, CancellationToken ct)
    {
        Lookups.Enqueue(clerkUserId);
        return Task.FromResult(Accounts.TryGetValue(clerkUserId, out var email) ? email : null);
    }
}

public sealed class FakePhotoStorage : IPhotoStorage
{
    public ConcurrentDictionary<string, StoredPhoto> Stored { get; } = new();

    public SignedUpload SignUpload(string publicId, DateTimeOffset now) =>
        new("https://api.cloudinary.com/v1_1/test/image/upload", new Dictionary<string, string>
        {
            ["public_id"] = publicId,
            ["signature"] = "test-signature",
        });

    public Task<StoredPhoto?> FindAsync(string publicId, CancellationToken ct) =>
        Task.FromResult(Stored.TryGetValue(publicId, out var photo) ? photo : null);

    public string CreateDownloadUrl(string publicId, DateTimeOffset now, TimeSpan validFor) =>
        $"https://api.cloudinary.com/v1_1/test/image/download?public_id={publicId}&expires_at={(now + validFor).ToUnixTimeSeconds()}";

    public Task<byte[]> DownloadForReadingAsync(string publicId, CancellationToken ct) =>
        Task.FromResult<byte[]>([0xFF, 0xD8, 0xFF, 0xD9]);
}

public sealed class FakeTranscriber : IEssayTranscriber
{
    public const string Model = "test-model";

    /// <summary>Respostas das próximas chamadas, na ordem; sem roteiro, a leitura sai limpa.</summary>
    public ConcurrentQueue<Func<TranscriptionCall>> Script { get; } = new();

    public static TranscriptionCall Reading(bool readable = true, bool identificationFound = false, params string[] lines) =>
        new(
            new TranscriptionReading(
                [.. lines.Select((text, index) => new ReadLine(index + 1, text, Indent: index == 0))],
                identificationFound,
                readable),
            Model,
            new AiTokens(Input: 5000, Output: 1500, CacheRead: 0, CacheWrite: 0));

    public static TranscriptionCall Invalid() => new(null, Model, new AiTokens(5000, 100, 0, 0));

    public static TranscriptionCall Unavailable() => throw Errors.AiUnavailable();

    /// <summary>O último pedido recebido, para conferir instruções e modelo.</summary>
    public TranscriptionRequest? LastRequest { get; private set; }

    public Task<TranscriptionCall> TranscribeAsync(TranscriptionRequest request, CancellationToken ct)
    {
        LastRequest = request;
        return Respond();
    }

    private Task<TranscriptionCall> Respond() =>
        Task.FromResult(Script.TryDequeue(out var next)
            ? next()
            : Reading(lines: ["A tecnologia mudou a forma de debater.", "Os algoritmos escolhem o que cada um vê."]));
}
