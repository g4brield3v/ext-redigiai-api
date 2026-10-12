using Microsoft.Extensions.DependencyInjection;
using RedigiAI.Application.Accounts;
using RedigiAI.Application.Billing;
using RedigiAI.Application.Catalog;
using RedigiAI.Application.Essays;
using RedigiAI.Application.Grading;

namespace RedigiAI.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddOptions<LegalOptions>();

        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<IClerkWebhookService, ClerkWebhookService>();
        services.AddScoped<ITestAccountService, TestAccountService>();
        services.AddScoped<ICreditService, CreditService>();
        services.AddScoped<ICatalogService, CatalogService>();
        services.AddScoped<ICatalogSeedService, CatalogSeedService>();
        services.AddScoped<GradingSeedService>();
        services.AddScoped<IUploadService, UploadService>();
        services.AddScoped<IEssayService, EssayService>();
        services.AddScoped<EssayOutcomes>();
        services.AddOptions<AiBudgetOptions>();
        services.AddSingleton<AiBudget>();
        services.AddScoped<Abstractions.IJobHandler, TranscribeEssayHandler>();

        return services;
    }
}
