using Microsoft.Extensions.DependencyInjection;
using RedigiAI.Application.Accounts;
using RedigiAI.Application.Billing;
using RedigiAI.Application.Catalog;
using RedigiAI.Application.Essays;
using RedigiAI.Application.Grading;
using RedigiAI.Application.Notifications;

namespace RedigiAI.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddOptions<LegalOptions>();

        services.AddScoped<IAdminAccessService, AdminAccessService>();
        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<IClerkWebhookService, ClerkWebhookService>();
        services.AddScoped<ICreditService, CreditService>();
        services.AddScoped<IPaymentService, PaymentService>();
        services.AddScoped<PlanCredits>();
        services.AddScoped<ISubscriptionService, SubscriptionService>();
        services.AddScoped<IRefundService, RefundService>();
        services.AddScoped<Abstractions.IJobHandler, ProcessRefundHandler>();
        services.AddScoped<Abstractions.IJobHandler, ProcessSubscriptionHandler>();
        services.AddScoped<PlanResume>();
        services.AddScoped<Accounts.IRegisteredAccess, Accounts.RegisteredAccess>();
        services.AddScoped<Catalog.IPromptImageMaintenance, Catalog.PromptImageMaintenance>();
        services.AddScoped<IPaymentWebhookService, PaymentWebhookService>();
        services.AddScoped<Abstractions.IJobHandler, ProcessPaymentHandler>();
        services.AddScoped<ICatalogService, CatalogService>();
        services.AddScoped<IPromptAdminService, PromptAdminService>();
        services.AddScoped<IPromptImportService, PromptImportService>();
        services.AddOptions<SeedOptions>();
        services.AddScoped<ICatalogSeedService, CatalogSeedService>();
        services.AddScoped<GradingSeedService>();
        services.AddScoped<IUploadService, UploadService>();
        services.AddScoped<IEssayService, EssayService>();
        services.AddScoped<EssayOutcomes>();
        services.AddScoped<ICorrectionService, CorrectionService>();
        services.AddOptions<AiBudgetOptions>()
            .Validate(
                ai => ai.MaxOutputTokens > 0
                    && ai.Grading.MaxTokens is > 0 && ai.Grading.MaxTokens <= ai.MaxOutputTokens
                    && ai.Transcription.MaxTokens is > 0 && ai.Transcription.MaxTokens <= ai.MaxOutputTokens,
                "Ai:Grading:MaxTokens e Ai:Transcription:MaxTokens vão de 1 até Ai:MaxOutputTokens.")
            .ValidateOnStart();
        services.AddOptions<CollectionCopyOptions>();
        services.AddSingleton<AiBudget>();
        services.AddScoped<AiSpending>();
        services.AddScoped<Abstractions.IJobHandler, TranscribeEssayHandler>();
        services.AddScoped<Abstractions.IJobHandler, GradeEssayHandler>();
        services.AddOptions<SiteOptions>();
        services.AddOptions<CompanyOptions>();
        services.AddSingleton<EmailComposer>();
        services.AddScoped<Abstractions.IJobHandler, SendEmailHandler>();

        return services;
    }
}
