using Microsoft.EntityFrameworkCore;
using RedigiAI.Domain.Accounts;
using RedigiAI.Domain.Auditing;
using RedigiAI.Domain.Billing;
using RedigiAI.Domain.Catalog;
using RedigiAI.Domain.Essays;
using RedigiAI.Domain.Jobs;

namespace RedigiAI.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Exam> Exams => Set<Exam>();
    public DbSet<EssayType> EssayTypes => Set<EssayType>();
    public DbSet<RubricVersion> RubricVersions => Set<RubricVersion>();
    public DbSet<Theme> Themes => Set<Theme>();
    public DbSet<User> Users => Set<User>();
    public DbSet<CreditBucket> CreditBuckets => Set<CreditBucket>();
    public DbSet<CreditLedgerEntry> CreditLedger => Set<CreditLedgerEntry>();
    public DbSet<Essay> Essays => Set<Essay>();
    public DbSet<EssayBatch> EssayBatches => Set<EssayBatch>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();
    public DbSet<AccessLogEntry> AccessLogs => Set<AccessLogEntry>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configuration)
    {
        configuration.Properties<ExamStatus>().HaveConversion<SnakeCaseEnumConverter<ExamStatus>>();
        configuration.Properties<ThemeStatus>().HaveConversion<SnakeCaseEnumConverter<ThemeStatus>>();
        configuration.Properties<ThemeTextKind>().HaveConversion<SnakeCaseEnumConverter<ThemeTextKind>>();
        configuration.Properties<CreditSource>().HaveConversion<SnakeCaseEnumConverter<CreditSource>>();
        configuration.Properties<EssayStatus>().HaveConversion<SnakeCaseEnumConverter<EssayStatus>>();
        configuration.Properties<JobStatus>().HaveConversion<SnakeCaseEnumConverter<JobStatus>>();
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
