using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Accounts;
using RedigiAI.Domain.Billing;
using RedigiAI.Domain.Catalog;
using RedigiAI.Domain.Essays;
using RedigiAI.Domain.Grading;
using RedigiAI.Infrastructure.Anthropic;
using RedigiAI.Infrastructure.Clerk;
using RedigiAI.Infrastructure.Cloudinary;
using RedigiAI.Infrastructure.Jobs;
using RedigiAI.Infrastructure.Persistence;
using RedigiAI.Infrastructure.Persistence.Repositories;

namespace RedigiAI.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddDbContext<AppDbContext>((provider, options) => options
            .UseNpgsql(provider.GetRequiredService<IConfiguration>().GetConnectionString("Default"))
            .UseSnakeCaseNamingConvention());

        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IDatabaseAdmin, DatabaseAdmin>();
        services.AddScoped<IAuditLog, AuditLog>();
        services.AddScoped<IAccessLog, AccessLog>();

        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<ICreditRepository, CreditRepository>();
        services.AddScoped<IExamRepository, ExamRepository>();
        services.AddScoped<IEssayTypeRepository, EssayTypeRepository>();
        services.AddScoped<IRubricRepository, RubricRepository>();
        services.AddScoped<IPromptRepository, PromptRepository>();

        services.AddScoped<IEssayRepository, EssayRepository>();
        services.AddScoped<IEssayBatchRepository, EssayBatchRepository>();
        services.AddScoped<IAssetRepository, AssetRepository>();
        services.AddScoped<ITranscriptionRepository, TranscriptionRepository>();
        services.AddScoped<IAiUsageRepository, AiUsageRepository>();
        services.AddScoped<IGradingProfileRepository, GradingProfileRepository>();
        services.AddScoped<IPromptTemplateRepository, PromptTemplateRepository>();
        services.AddScoped<IGuidelinesRepository, GuidelinesRepository>();
        services.AddScoped<IWritingRulesRepository, WritingRulesRepository>();
        services.AddScoped<ICorrectionRepository, CorrectionRepository>();
        services.AddScoped<IJobQueue, JobQueue>();
        services.AddSingleton<IJobRunner, JobRunner>();
        services.AddSingleton<JobSignal>();

        services.AddScoped<IClerkWebhookVerifier, ClerkWebhookVerifier>();
        services.AddSingleton<ClerkBackendApi>();
        services.AddSingleton<IIdentityDirectory>(provider => provider.GetRequiredService<ClerkBackendApi>());

        services.AddSingleton<IPhotoStorage, CloudinaryPhotoStorage>();
        services.AddSingleton<IPromptImageStorage, CloudinaryPromptImageStorage>();
        services.AddSingleton<ClaudeMessagesApi>();
        services.AddSingleton<IEssayTranscriber, ClaudeEssayTranscriber>();
        services.AddSingleton<IEssayGrader, ClaudeEssayGrader>();

        return services;
    }
}
