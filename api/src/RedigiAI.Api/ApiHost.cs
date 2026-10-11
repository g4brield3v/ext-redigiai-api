using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using RedigiAI.Api.Auth;
using RedigiAI.Api.Endpoints;
using RedigiAI.Api.Http;
using RedigiAI.Application;
using RedigiAI.Domain.Common;
using RedigiAI.Infrastructure;
using RedigiAI.Infrastructure.Persistence;

namespace RedigiAI.Api;

internal static class ApiHost
{
    private const long MaxRequestBodyBytes = 1024 * 1024;
    private const string CorsPolicy = "web";

    public static async Task<int> RunAsync(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.UseJsonConsole();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = MaxRequestBodyBytes);

        var services = builder.Services;
        services.AddApplication();
        services.AddInfrastructure();
        services.Configure<LegalOptions>(builder.Configuration.GetSection(LegalOptions.Section));
        services.AddClerkAuthentication(builder.Configuration);
        services.AddOpenApi();
        // Corpo ilegível vira exceção e sai no formato de erro, em qualquer ambiente.
        services.Configure<RouteHandlerOptions>(routes => routes.ThrowOnBadRequest = true);

        services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
            // Números só como números: o contrato publicado no openapi.json fica com tipos exatos.
            json.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
        });

        services.AddCors(cors => cors.AddPolicy(CorsPolicy, policy => policy
            .WithOrigins([.. CorsOrigins.From(builder.Configuration)])
            .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")
            .WithHeaders("Authorization", "Content-Type", "Idempotency-Key")
            .WithExposedHeaders("X-Trace-Id")));

        AddRateLimiting(services, builder.Configuration);

        // Atrás do proxy do Railway: o IP do aluno e o esquema vêm do último salto.
        if (builder.Configuration.GetValue<bool>("Proxy:TrustForwardedHeaders"))
        {
            services.Configure<ForwardedHeadersOptions>(forwarded =>
            {
                forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                forwarded.ForwardLimit = 1;
                forwarded.KnownIPNetworks.Clear();
                forwarded.KnownProxies.Clear();
            });
        }

        var app = builder.Build();

        if (app.Configuration.GetValue<bool>("Proxy:TrustForwardedHeaders"))
        {
            app.UseForwardedHeaders();
        }

        app.UseMiddleware<ErrorHandlingMiddleware>();
        app.UseMiddleware<SecurityHeadersMiddleware>();
        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }

        app.UseCors(CorsPolicy);
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi().DisableRateLimiting();
        }

        app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
            .AllowAnonymous().DisableRateLimiting().ExcludeFromDescription();
        app.MapGet("/ready", async (AppDbContext db, CancellationToken ct) =>
                await db.Database.CanConnectAsync(ct)
                    ? Results.Ok(new { status = "ready" })
                    : Results.Json(new { status = "unavailable" }, statusCode: 503))
            .AllowAnonymous().DisableRateLimiting().ExcludeFromDescription();

        app.MapCatalogEndpoints();
        app.MapAccountEndpoints();
        app.MapClerkWebhook();
        app.MapFallback(() => { throw Errors.NotFound(); }).AllowAnonymous();

        await app.RunAsync();
        return 0;
    }

    /// <summary>30 requisições por minuto por aluno; quem não está logado é limitado por IP.</summary>
    private static void AddRateLimiting(IServiceCollection services, IConfiguration configuration)
    {
        var perUser = configuration.GetValue("RateLimit:PerUserPerMinute", 30);
        var perIp = configuration.GetValue("RateLimit:PerIpPerMinute", 120);

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = async (context, _) =>
                await ErrorWriter.WriteAsync(context.HttpContext, Errors.RateLimited());
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
            {
                var userId = http.User.FindFirst("sub")?.Value;
                return userId is not null
                    ? Window($"user:{userId}", perUser)
                    : Window($"ip:{http.Connection.RemoteIpAddress}", perIp);
            });
        });

        static RateLimitPartition<string> Window(string key, int permits) =>
            RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permits,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
    }
}

internal static class LoggingSetup
{
    /// <summary>Logs estruturados em JSON, com os escopos (traceId) em toda linha.</summary>
    public static ILoggingBuilder UseJsonConsole(this ILoggingBuilder logging)
    {
        logging.ClearProviders();
        logging.AddJsonConsole(console =>
        {
            console.IncludeScopes = true;
            console.UseUtcTimestamp = true;
            console.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
        });
        return logging;
    }
}
