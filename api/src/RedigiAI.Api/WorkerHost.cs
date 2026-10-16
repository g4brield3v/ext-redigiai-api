using RedigiAI.Application;
using RedigiAI.Application.Abstractions;
using RedigiAI.Application.Catalog;
using RedigiAI.Infrastructure;
using RedigiAI.Application.Essays;
using RedigiAI.Application.Grading;
using RedigiAI.Infrastructure.Anthropic;
using RedigiAI.Infrastructure.Cloudinary;

namespace RedigiAI.Api;

internal static class WorkerHost
{
    public static async Task<int> RunAsync(string[] args)
    {
        var builder = CreateBuilder(args);
        builder.Services.AddHostedService<JobWorker>();
        builder.Services.AddHostedService<ReviewDeadlineSweeper>();

        await builder.Build().RunAsync();
        return 0;
    }

    public static HostApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        // Worker, migrate e seed também se recusam a subir com o acesso de desenvolvimento ligado fora de Development.
        _ = Auth.DevAuthentication.IsEnabled(builder.Configuration, builder.Environment);
        builder.Logging.UseJsonConsole();
        builder.Services.AddApplication();
        builder.Services.AddInfrastructure();
        builder.Services.Configure<LegalOptions>(builder.Configuration.GetSection(LegalOptions.Section));
        builder.Services.Configure<SeedOptions>(seed =>
            seed.ExamplePrompt = seed.ReplaceOutdatedInstructions = builder.Environment.IsDevelopment());
        builder.Services.Configure<CloudinaryOptions>(builder.Configuration.GetSection(CloudinaryOptions.Section));
        builder.Services.Configure<AnthropicOptions>(builder.Configuration.GetSection(AnthropicOptions.Section));
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

    public static async Task<int> SeedAsync(string[] args)
    {
        using var host = WorkerHost.CreateBuilder(args).Build();
        await using var scope = host.Services.CreateAsyncScope();
        var seeded = await scope.ServiceProvider.GetRequiredService<ICatalogSeedService>().SeedAsync(CancellationToken.None);
        scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("RedigiAI.Api.Seed")
            .LogInformation(seeded ? "Catalog seeded" : "Catalog already present; nothing to do");
        return 0;
    }
}
