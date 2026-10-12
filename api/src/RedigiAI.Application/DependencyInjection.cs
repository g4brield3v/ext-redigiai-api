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

        services.AddOptions<AdminOptions>();
        services.AddScoped<AdminRole>();
        services.AddScoped<IAdminAccessService, AdminAccessService>();
        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<IClerkWebhookService, ClerkWebhookService>();
        services.AddScoped<ITestAccountService, TestAccountService>();
        services.AddScoped<ICreditService, CreditService>();
        services.AddScoped<ICatalogService, CatalogService>();
        services.AddScoped<IPromptAdminService, PromptAdminService>();
        services.AddOptions<SeedOptions>();
        services.AddScoped<ICatalogSeedService, CatalogSeedService>();
        services.AddScoped<GradingSeedService>();
        services.AddScoped<IUploadService, UploadService>();
        services.AddScoped<IEssayService, EssayService>();
        services.AddScoped<EssayOutcomes>();
        services.AddScoped<ICorrectionService, CorrectionService>();
        services.AddOptions<AiBudgetOptions>();
        services.AddSingleton<AiBudget>();
        services.AddScoped<AiSpending>();
        services.AddScoped<Abstractions.IJobHandler, TranscribeEssayHandler>();
        services.AddScoped<Abstractions.IJobHandler, GradeEssayHandler>();

        return services;
    }
}
