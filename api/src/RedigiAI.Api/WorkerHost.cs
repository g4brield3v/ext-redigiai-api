using RedigiAI.Application;
using RedigiAI.Application.Abstractions;
using RedigiAI.Application.Catalog;
using RedigiAI.Infrastructure;
using RedigiAI.Application.Essays;
using RedigiAI.Application.Grading;
using RedigiAI.Application.Notifications;
using RedigiAI.Infrastructure.Anthropic;
using RedigiAI.Infrastructure.Cloudinary;
using RedigiAI.Infrastructure.Email;

namespace RedigiAI.Api;

internal static class WorkerHost
{
    public static async Task<int> RunAsync(string[] args)
    {
        var builder = CreateBuilder(args);
        builder.Services.AddHostedService<JobWorker>();
        builder.Services.AddHostedService<ReviewDeadlineSweeper>();
        builder.Services.AddHostedService<CreditExpirySweeper>();
        builder.Services.AddHostedService<PaymentReconciliationSweeper>();

        await builder.Build().RunAsync();
        return 0;
    }

    public static HostApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.UseJsonConsole();
        builder.Services.AddApplication();
        builder.Services.AddInfrastructure();
        builder.Services.Configure<LegalOptions>(builder.Configuration.GetSection(LegalOptions.Section));
        builder.Services.Configure<SeedOptions>(seed =>
            seed.ReplaceOutdatedInstructions = builder.Environment.IsDevelopment());
        builder.Services.Configure<CloudinaryOptions>(builder.Configuration.GetSection(CloudinaryOptions.Section));
        builder.Services.Configure<AnthropicOptions>(builder.Configuration.GetSection(AnthropicOptions.Section));
        builder.Services.Configure<RedigiAI.Infrastructure.MercadoPago.MercadoPagoOptions>(
            builder.Configuration.GetSection(RedigiAI.Infrastructure.MercadoPago.MercadoPagoOptions.Section));
        // O pagador de teste só existe no desenvolvimento; em outro ambiente, a API não sobe.
        builder.Services.AddOptions<RedigiAI.Infrastructure.MercadoPago.MercadoPagoOptions>()
            .Validate(
                mercadoPago => mercadoPago.TestPayerEmail.Length == 0 || builder.Environment.IsDevelopment(),
                "MercadoPago__TestPayerEmail is only allowed in Development.")
            .ValidateOnStart();
        // Aceita também o nome padrão da Anthropic para a chave.
        builder.Services.PostConfigure<AnthropicOptions>(anthropic =>
        {
            if (anthropic.ApiKey.Length == 0)
            {
                anthropic.ApiKey = builder.Configuration["ANTHROPIC_API_KEY"] ?? "";
            }
        });
        builder.Services.Configure<AiBudgetOptions>(builder.Configuration.GetSection(AiBudgetOptions.Section));
        builder.Services.Configure<CollectionCopyOptions>(builder.Configuration.GetSection(CollectionCopyOptions.Section));
        builder.Services.Configure<SiteOptions>(builder.Configuration.GetSection(SiteOptions.Section));
        builder.Services.PostConfigure<SiteOptions>(site => site.RequireHttps = !builder.Environment.IsDevelopment());
        builder.Services.Configure<CompanyOptions>(builder.Configuration.GetSection(CompanyOptions.Section));
        builder.Services.Configure<SendGridOptions>(builder.Configuration.GetSection(SendGridOptions.Section));
        builder.Services.AddEmailSender(builder.Environment.IsDevelopment());
        builder.Services.Configure<RedigiAI.Infrastructure.Jobs.JobQueueOptions>(
            builder.Configuration.GetSection(RedigiAI.Infrastructure.Jobs.JobQueueOptions.Section));
        return builder;
    }
}

internal static class DatabaseCommands
{
    /// <summary>Aplica as migrações pendentes. Roda no deploy, antes de subir a nova versão.</summary>
    public static async Task<int> MigrateAsync(string[] args)
    {
        using var host = WorkerHost.CreateBuilder(args).Build();
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseAdmin>().MigrateAsync(CancellationToken.None);
        return 0;
    }

    /// <summary>
    /// Carga inicial do catálogo. Com <c>--publish-instructions</c>, também fora de Development: se as instruções de
    /// correção ou o manual do programa mudaram, publica a versão seguinte e troca os perfis ativos (roda no deploy,
    /// depois do <c>migrate</c>).
    /// </summary>
    public static async Task<int> SeedAsync(string[] args)
    {
        const string PublishInstructions = "--publish-instructions";
        var builder = WorkerHost.CreateBuilder([.. args.Where(arg => arg != PublishInstructions)]);
        if (args.Contains(PublishInstructions))
        {
            builder.Services.PostConfigure<SeedOptions>(seed => seed.ReplaceOutdatedInstructions = true);
        }

        using var host = builder.Build();
        await using var scope = host.Services.CreateAsyncScope();
        var seeded = await scope.ServiceProvider.GetRequiredService<ICatalogSeedService>().SeedAsync(CancellationToken.None);
        scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("RedigiAI.Api.Seed")
            .LogInformation(seeded ? "Catalog seeded" : "Catalog already present; nothing to do");
        return 0;
    }

    /// <summary>Torna privadas as imagens de proposta enviadas como públicas antes de 20/10/2026.</summary>
    public static async Task<int> MakePromptImagesPrivateAsync(string[] args)
    {
        using var host = WorkerHost.CreateBuilder(args).Build();
        await using var scope = host.Services.CreateAsyncScope();
        var (changed, total) = await scope.ServiceProvider.GetRequiredService<RedigiAI.Application.Catalog.IPromptImageMaintenance>()
            .MakeAllPrivateAsync(CancellationToken.None);
        scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("RedigiAI.Api.PromptImages")
            .LogInformation("Prompt images made private: {Changed} of {Total}", changed, total);
        return 0;
    }

    /// <summary>
    /// Publica as propostas que vêm com o programa e ainda não existem neste ambiente:
    /// <c>import-prompts --admin user_…</c>, com o <c>clerk_user_id</c> de um administrador. Pode rodar de novo.
    /// </summary>
    public static async Task<int> ImportPromptsAsync(string[] args)
    {
        var at = Array.IndexOf(args, "--admin");
        var admin = at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
        var rest = at >= 0 ? [.. args[..at], .. args[Math.Min(at + 2, args.Length)..]] : args;

        using var host = WorkerHost.CreateBuilder(rest).Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("RedigiAI.Api.PromptImport");
        if (string.IsNullOrWhiteSpace(admin))
        {
            logger.LogError("Usage: import-prompts --admin <clerk_user_id of an administrator>");
            return 2;
        }

        await using var scope = host.Services.CreateAsyncScope();
        var results = await scope.ServiceProvider.GetRequiredService<IPromptImportService>()
            .ImportAsync(admin, BundledPrompts.Packages(), CancellationToken.None);
        foreach (var result in results)
        {
            if (result.Outcome == PromptImportOutcome.Failed)
            {
                logger.LogError(
                    "Prompt {Package} not imported: {Problems}", result.Package, string.Join(" | ", result.Problems));
            }
            else
            {
                logger.LogInformation("Prompt {Package} ({Slug}): {Outcome}", result.Package, result.Slug, result.Outcome);
            }
        }

        return results.Any(result => result.Outcome == PromptImportOutcome.Failed) ? 1 : 0;
    }
}
