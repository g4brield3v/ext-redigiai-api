using Microsoft.EntityFrameworkCore;
using RedigiAI.Domain.Accounts;
using RedigiAI.Domain.Auditing;
using RedigiAI.Domain.Billing;
using RedigiAI.Domain.Catalog;
using RedigiAI.Domain.Common;
using RedigiAI.Domain.Essays;
using RedigiAI.Domain.Grading;
using RedigiAI.Domain.Jobs;
using RedigiAI.Domain.Notifications;

namespace RedigiAI.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Exam> Exams => Set<Exam>();
    public DbSet<EssayType> EssayTypes => Set<EssayType>();
    public DbSet<RubricVersion> RubricVersions => Set<RubricVersion>();
    public DbSet<Prompt> Prompts => Set<Prompt>();
    public DbSet<User> Users => Set<User>();
    public DbSet<CreditBucket> CreditBuckets => Set<CreditBucket>();
    public DbSet<CreditLedgerEntry> CreditLedger => Set<CreditLedgerEntry>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();
    public DbSet<Essay> Essays => Set<Essay>();
    public DbSet<EssayBatch> EssayBatches => Set<EssayBatch>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<Transcription> Transcriptions => Set<Transcription>();
    public DbSet<IllegibleWord> IllegibleWords => Set<IllegibleWord>();
    public DbSet<AiUsage> AiUsages => Set<AiUsage>();
    public DbSet<PromptTemplate> PromptTemplates => Set<PromptTemplate>();
    public DbSet<GuidelinesVersion> GuidelinesVersions => Set<GuidelinesVersion>();
    public DbSet<WritingRulesVersion> WritingRulesVersions => Set<WritingRulesVersion>();
    public DbSet<GradingProfile> GradingProfiles => Set<GradingProfile>();
    public DbSet<Correction> Corrections => Set<Correction>();
    public DbSet<CorrectionFeedback> CorrectionFeedback => Set<CorrectionFeedback>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();
    public DbSet<AccessLogEntry> AccessLogs => Set<AccessLogEntry>();
    public DbSet<EmailLogEntry> EmailLog => Set<EmailLogEntry>();
    public DbSet<FeatureFlag> FeatureFlags => Set<FeatureFlag>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configuration)
    {
        configuration.Properties<ExamStatus>().HaveConversion<SnakeCaseEnumConverter<ExamStatus>>();
        configuration.Properties<PromptStatus>().HaveConversion<SnakeCaseEnumConverter<PromptStatus>>();
        configuration.Properties<PromptBlockKind>().HaveConversion<SnakeCaseEnumConverter<PromptBlockKind>>();
        configuration.Properties<AssetScope>().HaveConversion<SnakeCaseEnumConverter<AssetScope>>();
        configuration.Properties<UserRole>().HaveConversion<SnakeCaseEnumConverter<UserRole>>();
        configuration.Properties<CreditSource>().HaveConversion<SnakeCaseEnumConverter<CreditSource>>();
        configuration.Properties<SubscriptionStatus>().HaveConversion<SnakeCaseEnumConverter<SubscriptionStatus>>();
        configuration.Properties<PaymentKind>().HaveConversion<SnakeCaseEnumConverter<PaymentKind>>();
        configuration.Properties<PaymentMethod>().HaveConversion<SnakeCaseEnumConverter<PaymentMethod>>();
        configuration.Properties<PaymentStatus>().HaveConversion<SnakeCaseEnumConverter<PaymentStatus>>();
        configuration.Properties<EssayStatus>().HaveConversion<SnakeCaseEnumConverter<EssayStatus>>();
        configuration.Properties<JobStatus>().HaveConversion<SnakeCaseEnumConverter<JobStatus>>();
        configuration.Properties<PromptKind>().HaveConversion<SnakeCaseEnumConverter<PromptKind>>();
        configuration.Properties<GradingProfileStatus>().HaveConversion<SnakeCaseEnumConverter<GradingProfileStatus>>();
        configuration.Properties<MarkKind>().HaveConversion<SnakeCaseEnumConverter<MarkKind>>();
        configuration.Properties<FeedbackRating>().HaveConversion<SnakeCaseEnumConverter<FeedbackRating>>();
        configuration.Properties<BirthDate>().HaveConversion<BirthDateConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // As chaves são UUID v7 gerados no domínio; o banco nunca gera.
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.FindPrimaryKey()?.Properties ?? [])
            {
                property.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
            }
        }
    }
}
