using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using RedigiAI.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;
using RedigiAI.Api.Http;
using RedigiAI.Application;
using RedigiAI.Domain.Common;
using RedigiAI.Infrastructure;

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
        var devAccess = DevAuthentication.IsEnabled(builder.Configuration, builder.Environment);
        services.AddClerkAuthentication(builder.Configuration, devAccess);
        if (devAccess)
        {
            services.AddHostedService<DevEnvironmentSetup>();
        }
        AddControllers(services);
        AddOpenApiDocument(services);


        services.AddCors(cors => cors.AddPolicy(CorsPolicy, policy => policy
            .WithOrigins([.. CorsOrigins.From(builder.Configuration)])
            .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")
            .WithHeaders(["Authorization", "Content-Type", "Idempotency-Key", .. devAccess ? new[] { DevAuthentication.Header } : []])
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

        if (app.Environment.IsDevelopment())
        {
            // Documentação navegável em /swagger, só em desenvolvimento.
            app.MapOpenApi().DisableRateLimiting();
            app.UseSwaggerUI(swagger =>
            {
                swagger.SwaggerEndpoint("/openapi/v1.json", "RedigiAI API v1");
                swagger.DocumentTitle = "RedigiAI API";
            });
        }

        app.UseCors(CorsPolicy);
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();

        app.MapControllers();
        app.MapFallback(() => { throw Errors.NotFound(); }).AllowAnonymous();

        await app.RunAsync();
        return 0;
    }

    private static void AddControllers(IServiceCollection services)
    {
        services.AddControllers()
            .AddJsonOptions(json => ConfigureJson(json.JsonSerializerOptions))
            .ConfigureApiBehaviorOptions(behavior =>
            {
                // Corpo ilegível ou inválido sai no formato de erro da API, não como ProblemDetails.
                behavior.SuppressMapClientErrors = true;
                behavior.InvalidModelStateResponseFactory = _ =>
                    throw Errors.Validation("Não foi possível ler os dados enviados.");
            });

        // O documento OpenAPI lê as opções de JSON das APIs mínimas: as duas precisam ser iguais.
        services.ConfigureHttpJsonOptions(json => ConfigureJson(json.SerializerOptions));
    }

    private static void ConfigureJson(JsonSerializerOptions options)
    {
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        // Números só como números: o contrato publicado no openapi.json fica com tipos exatos.
        options.NumberHandling = JsonNumberHandling.Strict;
    }

    /// <summary>Documento em /openapi/v1.json, com o esquema Bearer nas rotas que exigem login.</summary>
    private static void AddOpenApiDocument(IServiceCollection services)
    {
        const string scheme = "Bearer";

        services.AddOpenApi(openApi =>
        {
            openApi.AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = "RedigiAI API";
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes[scheme] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    Description = "Token de sessão do Clerk.",
                };
                return Task.CompletedTask;
            });

            openApi.AddOperationTransformer((operation, context, _) =>
            {
                var metadata = context.Description.ActionDescriptor.EndpointMetadata;
                if (metadata.OfType<IAuthorizeData>().Any() && !metadata.OfType<IAllowAnonymous>().Any())
                {
                    operation.Security ??= [];
                    operation.Security.Add(new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference(scheme, context.Document)] = [],
                    });
                }

                return Task.CompletedTask;
            });
        });
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
