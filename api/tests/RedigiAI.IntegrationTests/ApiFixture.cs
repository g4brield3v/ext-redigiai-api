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
using RedigiAI.Application.Notifications;
using RedigiAI.Domain.Billing;
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

    /// <summary>O servidor de banco dos testes, para quem precisa de um banco à parte.</summary>
    public string ConnectionString => _database.GetConnectionString();

    /// <summary>Faz o papel do Cloudinary: o que o teste "enviou" e a assinatura devolvida ao navegador.</summary>
    public FakePhotoStorage Photos { get; } = new();

    /// <summary>Faz o papel do Claude na transcrição: devolve o que o teste roteirizar.</summary>
    public FakeTranscriber Transcriber { get; } = new();

    /// <summary>Faz o papel do Claude na correção: devolve o JSON que o teste roteirizar.</summary>
    public FakeGrader Grader { get; } = new();

    /// <summary>Faz o papel da pasta pública do Cloudinary, onde ficam as imagens das propostas.</summary>
    public FakePromptImageStorage PromptImages { get; } = new();

    /// <summary>Faz o papel da Backend API do Clerk: o e-mail principal de cada conta e se está verificado.</summary>
    public FakeIdentityDirectory Directory { get; } = new();

    public FakeEmailSender Emails { get; } = new();

    /// <summary>O Mercado Pago dos testes: nenhuma chamada sai da máquina.</summary>
    public FakePaymentGateway Payments { get; } = new();

    public const string MercadoPagoSecret = "mp-webhook-test-secret";

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

    /// <param name="examplePrompt">
    /// Os testes usam a proposta de exemplo da carga inicial, que fora deles só existe em Development. Com
    /// <c>false</c>, vale o que o programa decide pelo ambiente.
    /// </param>
    public WebApplicationFactory<Program> CreateFactory(Action<IWebHostBuilder>? configure = null, bool examplePrompt = true) =>
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
            builder.UseSetting("App:BaseUrl", WebOrigin);
            builder.UseSetting("MercadoPago:WebhookSecret", MercadoPagoSecret);
            builder.UseSetting("MercadoPago:AccessToken", "");
            configure?.Invoke(builder);

            builder.ConfigureTestServices(services =>
            {
                if (examplePrompt)
                {
                    services.Configure<SeedOptions>(seed => seed.ExamplePrompt = true);
                }

                services.AddSingleton<IPhotoStorage>(Photos);
                services.AddSingleton<IEssayTranscriber>(Transcriber);
                services.AddSingleton<IEssayGrader>(Grader);
                services.AddSingleton<IIdentityDirectory>(Directory);
                services.AddSingleton<IPromptImageStorage>(PromptImages);
                services.AddSingleton<IEmailSender>(Emails);
                services.AddSingleton<IPaymentGateway>(Payments);
                // O site dos testes é o do Vite, em http.
                services.PostConfigure<SiteOptions>(site => site.RequireHttps = false);
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

    /// <summary>Uma notificação do Mercado Pago, assinada como ele assina (ou com outra chave).</summary>
    public static HttpRequestMessage CreateMercadoPagoWebhook(
        string resourceId, string json, string type = "payment", string? secret = null, string? requestId = "req-1",
        DateTimeOffset? signedAt = null)
    {
        var ts = (signedAt ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var manifest = requestId is null
            ? $"id:{resourceId.ToLowerInvariant()};ts:{ts};"
            : $"id:{resourceId.ToLowerInvariant()};request-id:{requestId};ts:{ts};";
        var v1 = Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(secret ?? MercadoPagoSecret), System.Text.Encoding.UTF8.GetBytes(manifest)));
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/webhooks/mercadopago?data.id={resourceId}&type={type}")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-signature", $"ts={ts},v1={v1}");
        if (requestId is not null)
        {
            request.Headers.Add("x-request-id", requestId);
        }
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

    /// <summary>Contas para as quais o provedor não responde agora (fora do ar, limite de chamadas).</summary>
    public ConcurrentDictionary<string, bool> Unavailable { get; } = new();

    public Task<PrimaryEmail?> FindPrimaryEmailAsync(string clerkUserId, CancellationToken ct)
    {
        Lookups.Enqueue(clerkUserId);
        if (Unavailable.ContainsKey(clerkUserId))
        {
            throw new IdentityDirectoryUnavailableException("test");
        }

        return Task.FromResult(Accounts.TryGetValue(clerkUserId, out var email) ? email : null);
    }
}

public sealed class FakePromptImageStorage : IPromptImageStorage
{
    /// <summary>O que foi enviado, pelo identificador: o tipo detectado e o conteúdo.</summary>
    public ConcurrentDictionary<string, (string ContentType, byte[] Content)> Uploaded { get; } = new();

    public Task<StoredImage> UploadAsync(string publicId, byte[] content, string contentType, CancellationToken ct)
    {
        Uploaded[publicId] = (contentType, content);
        return Task.FromResult(new StoredImage(content.Length, 800, 240));
    }

    public string PublicUrl(string publicId) => $"https://res.cloudinary.com/test/image/upload/{publicId}";
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

    /// <summary>Resposta cortada no <c>max_tokens</c>: sem leitura, com o consumo cobrado.</summary>
    public static TranscriptionCall Truncated() => new(null, Model, new AiTokens(5000, 8000, 0, 0), Truncated: true);

    /// <summary>O último pedido recebido, para conferir instruções e modelo.</summary>
    public TranscriptionRequest? LastRequest { get; private set; }

    public ConcurrentQueue<TranscriptionRequest> Requests { get; } = new();

    public Task<TranscriptionCall> TranscribeAsync(TranscriptionRequest request, CancellationToken ct)
    {
        LastRequest = request;
        Requests.Enqueue(request);
        return Respond();
    }

    private Task<TranscriptionCall> Respond() =>
        Task.FromResult(Script.TryDequeue(out var next)
            ? next()
            : Reading(lines: ["A tecnologia mudou a forma de debater.", "Os algoritmos escolhem o que cada um vê."]));
}

/// <summary>Guarda os e-mails em vez de enviá-los. O roteiro faz as próximas chamadas falharem, na ordem.</summary>
public sealed class FakeEmailSender : IEmailSender
{
    public ConcurrentQueue<EmailMessage> Sent { get; } = new();

    public ConcurrentQueue<Exception> Failures { get; } = new();

    public IReadOnlyList<EmailMessage> For(Guid essayId) => [.. Sent.Where(message => message.RefId == essayId)];

    public Task<string?> SendAsync(EmailMessage message, CancellationToken ct)
    {
        if (Failures.TryDequeue(out var failure))
        {
            throw failure;
        }

        Sent.Enqueue(message);
        return Task.FromResult<string?>($"fake-{Guid.NewGuid():N}");
    }
}

/// <summary>
/// O Mercado Pago dos testes. A criação com o mesmo id de compra devolve o mesmo pagamento, como a chave de
/// idempotência faz lá; a situação de cada pagamento é a que o teste definir.
/// </summary>
public sealed class FakePaymentGateway : IPaymentGateway
{
    private readonly ConcurrentDictionary<Guid, string> _byPayment = new();
    private readonly ConcurrentDictionary<string, ProviderPayment> _remote = new();
    private readonly ConcurrentDictionary<Guid, string> _bySubscription = new();
    private readonly ConcurrentDictionary<string, string> _byCreationKey = new();
    private readonly ConcurrentDictionary<string, ProviderSubscription> _subscriptions = new();
    private readonly ConcurrentDictionary<string, ProviderSubscriptionCharge> _charges = new();
    private int _subscriptionCreates;

    /// <summary>Quantas vezes a criação de assinatura chegou "lá".</summary>
    public int SubscriptionCreates => _subscriptionCreates;

    public string? PreapprovalIdOf(Guid subscriptionId) => _bySubscription.TryGetValue(subscriptionId, out var id) ? id : null;

    public ProviderSubscription SubscriptionThere(string preapprovalId) => _subscriptions[preapprovalId];

    /// <summary>As assinaturas "lá" com a referência desta assinatura daqui, canceladas ou não.</summary>
    public IReadOnlyList<ProviderSubscription> SubscriptionsFor(Guid subscriptionId) =>
        _subscriptions.Values.Where(s => s.ExternalReference == subscriptionId.ToString()).ToList();

    /// <summary>A próxima criação de assinatura é aceita lá, mas a resposta não chega (tempo esgotado).</summary>
    public bool LoseNextSubscriptionResponse { get; set; }

    /// <summary>Muda a assinatura "lá", como o aluno autorizando o cartão ou o Mercado Pago pausando.</summary>
    public void SetSubscription(string preapprovalId, SubscriptionStatus status, DateTimeOffset? nextChargeAt = null, string? cardLast4 = null)
    {
        var current = _subscriptions[preapprovalId];
        _subscriptions[preapprovalId] = current with
        {
            Status = status,
            NextChargeAt = nextChargeAt ?? current.NextChargeAt,
            CardLast4 = cardLast4 ?? current.CardLast4,
        };
    }

    /// <summary>Uma cobrança mensal da assinatura "lá", com o pagamento dela na situação dada.</summary>
    /// <returns>O id da cobrança (para o aviso) e o do pagamento.</returns>
    public (string ChargeId, string PaymentId) Charge(string preapprovalId, PaymentStatus status, long amountCents = 3990)
    {
        var paymentId = Random.Shared.NextInt64(1_000_000_000, 9_000_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var chargeId = Random.Shared.NextInt64(1_000_000_000, 9_000_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _remote[paymentId] = new ProviderPayment(
            paymentId, status, _subscriptions[preapprovalId].ExternalReference, amountCents, "BRL", PreapprovalId: preapprovalId);
        _charges[chargeId] = new ProviderSubscriptionCharge(chargeId, preapprovalId, paymentId);
        return (chargeId, paymentId);
    }

    /// <summary>Um token de cartão que o simulado recusa, como um cartão sem limite.</summary>
    public const string DeclinedCardToken = "declined";

    public Task<CreatedSubscription> CreateSubscriptionAsync(SubscriptionRequest request, CancellationToken ct)
    {
        if (Failures.TryDequeue(out var failure))
        {
            throw failure;
        }

        if (request.Card.Token == DeclinedCardToken)
        {
            throw Errors.PaymentFailed();
        }

        // Como lá: a assinatura nasce autorizada com o cartão (checkout transparente), e só a mesma chave de
        // idempotência devolve a mesma; outra chave cria outra.
        var id = _byCreationKey.GetOrAdd(request.IdempotencyKey, _ =>
        {
            Interlocked.Increment(ref _subscriptionCreates);
            var preapprovalId = $"pre{Guid.NewGuid():N}";
            _subscriptions[preapprovalId] = new ProviderSubscription(
                preapprovalId, SubscriptionStatus.Active, request.SubscriptionId.ToString(), DateTimeOffset.UtcNow.AddMonths(1), "4242");
            _bySubscription.TryAdd(request.SubscriptionId, preapprovalId);
            return preapprovalId;
        });
        if (LoseNextSubscriptionResponse)
        {
            LoseNextSubscriptionResponse = false;
            throw Errors.PaymentUnavailable();
        }

        return Task.FromResult(new CreatedSubscription(id));
    }

    public Task<ProviderSubscription?> FindSubscriptionByReferenceAsync(Guid subscriptionId, CancellationToken ct) =>
        Task.FromResult(SubscriptionsFor(subscriptionId).FirstOrDefault(s => s.Status != SubscriptionStatus.Cancelled));

    public Task<ProviderSubscription?> GetSubscriptionAsync(string providerId, CancellationToken ct) =>
        Task.FromResult(_subscriptions.TryGetValue(providerId, out var subscription) ? subscription : null);

    public Task<ProviderSubscriptionCharge?> GetSubscriptionChargeAsync(string providerId, CancellationToken ct) =>
        Task.FromResult(_charges.TryGetValue(providerId, out var charge) ? charge : null);

    /// <summary>Os reembolsos feitos "lá", pela chave de idempotência: a mesma chave conta uma vez só.</summary>
    public ConcurrentDictionary<Guid, (string PaymentId, long AmountCents)> Refunds { get; } = new();

    public Task<string> RefundAsync(string paymentProviderId, long amountCents, Guid idempotencyKey, CancellationToken ct)
    {
        Refunds.TryAdd(idempotencyKey, (paymentProviderId, amountCents));
        return Task.FromResult($"refund-{idempotencyKey:N}");
    }

    public Task CancelSubscriptionAsync(string providerId, CancellationToken ct)
    {
        SetSubscription(providerId, SubscriptionStatus.Cancelled);
        return Task.CompletedTask;
    }
    private int _creates;

    /// <summary>Quantas vezes a criação chegou "lá".</summary>
    public int Creates => _creates;

    /// <summary>A situação dos próximos cartões na criação; o padrão é "em análise" (pendente).</summary>
    public PaymentStatus NextCardStatus { get; set; } = PaymentStatus.Pending;

    /// <summary>O roteiro faz as próximas criações falharem, na ordem.</summary>
    public ConcurrentQueue<Exception> Failures { get; } = new();

    public ConcurrentQueue<(ChargeRequest Request, CardToken? Card)> Requests { get; } = new();

    public string? ProviderIdOf(Guid paymentId) => _byPayment.TryGetValue(paymentId, out var id) ? id : null;

    /// <summary>Muda o pagamento "lá", como o aluno pagando, o banco estornando ou um chargeback.</summary>
    public void Set(
        string providerId, PaymentStatus? status = null, long? amountCents = null, string? currency = null, long? refundedCents = null)
    {
        var current = _remote[providerId];
        _remote[providerId] = current with
        {
            Status = status ?? current.Status,
            AmountCents = amountCents ?? current.AmountCents,
            Currency = currency ?? current.Currency,
            RefundedCents = refundedCents ?? current.RefundedCents,
        };
    }

    public Task<CreatedCharge> CreatePixAsync(ChargeRequest request, DateTimeOffset expiresAt, CancellationToken ct) =>
        Task.FromResult(Create(request, null, PaymentStatus.Pending) with
        {
            PixQrCode = $"00020126pix-{request.PaymentId:N}",
            PixQrCodeBase64 = "iVBORw0KGgo=",
            PixExpiresAt = expiresAt,
        });

    public Task<CreatedCharge> CreateCardAsync(ChargeRequest request, CardToken card, CancellationToken ct) =>
        Task.FromResult(Create(request, card, NextCardStatus));

    public Task<ProviderPayment?> GetPaymentAsync(string providerId, CancellationToken ct) =>
        Task.FromResult(_remote.TryGetValue(providerId, out var payment) ? payment : null);

    private CreatedCharge Create(ChargeRequest request, CardToken? card, PaymentStatus status)
    {
        if (Failures.TryDequeue(out var failure))
        {
            throw failure;
        }

        Requests.Enqueue((request, card));
        var providerId = _byPayment.GetOrAdd(request.PaymentId, _ =>
        {
            Interlocked.Increment(ref _creates);
            // Como lá: a compra avulsa é uma order, com id em maiúsculas (ORD…).
            var id = $"ORDTST{Guid.NewGuid():N}".ToUpperInvariant()[..32];
            _remote[id] = new ProviderPayment(id, status, request.PaymentId.ToString(), request.AmountCents, "BRL");
            return id;
        });
        return new CreatedCharge(providerId, _remote[providerId].Status);
    }
}
