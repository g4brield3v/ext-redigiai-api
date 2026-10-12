using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RedigiAI.Domain.Accounts;
using RedigiAI.Domain.Auditing;
using RedigiAI.Domain.Billing;
using RedigiAI.Domain.Essays;

namespace RedigiAI.Infrastructure.Persistence.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");
        builder.Property(u => u.ClerkUserId).HasMaxLength(64);
        builder.Property(u => u.Email).HasMaxLength(320);
        builder.Property(u => u.Name).HasMaxLength(User.MaxNameLength);
        builder.Property(u => u.TermsVersion).HasMaxLength(20);
        builder.Property(u => u.Role).HasMaxLength(20).HasDefaultValue(UserRole.Student);
        builder.Property(u => u.RoleCheckedSessionId).HasMaxLength(64);
        builder.ComplexProperty(u => u.EmailPreferences, preferences =>
        {
            preferences.Property(p => p.CorrectionReady).HasColumnName("notify_correction_ready");
            preferences.Property(p => p.IllegibleReminder).HasColumnName("notify_illegible_reminder");
            preferences.Property(p => p.NewThemes).HasColumnName("notify_new_themes");
        });
        builder.HasIndex(u => u.ClerkUserId).IsUnique();
        builder.HasIndex(u => u.LastActiveAt);
    }
}

internal sealed class CreditBucketConfiguration : IEntityTypeConfiguration<CreditBucket>
{
    public void Configure(EntityTypeBuilder<CreditBucket> builder)
    {
        builder.ToTable("credit_buckets", table =>
        {
            table.HasCheckConstraint("ck_credit_buckets_remaining", "remaining >= 0 AND remaining <= granted");
        });
        builder.Property(b => b.Source).HasMaxLength(20);
        builder.HasOne<User>().WithMany().HasForeignKey(b => b.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(b => b.UserId);

        // Teste grátis: no máximo um por conta, garantido pelo banco.
        builder.HasIndex(b => b.UserId, "ux_credit_buckets_trial_per_user")
            .HasDatabaseName("ux_credit_buckets_trial_per_user")
            .IsUnique()
            .HasFilter("source = 'trial'");
    }
}

internal sealed class CreditLedgerEntryConfiguration : IEntityTypeConfiguration<CreditLedgerEntry>
{
    public void Configure(EntityTypeBuilder<CreditLedgerEntry> builder)
    {
        builder.ToTable("credit_ledger");
        builder.Property(e => e.Source).HasMaxLength(20);
        builder.HasOne<User>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CreditBucket>().WithMany().HasForeignKey(e => e.BucketId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Essay>().WithMany().HasForeignKey(e => e.EssayId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(e => e.UserId);
    }
}

internal sealed class AuditLogEntryConfiguration : IEntityTypeConfiguration<AuditLogEntry>
{
    public void Configure(EntityTypeBuilder<AuditLogEntry> builder)
    {
        builder.ToTable("audit_log");
        builder.Property(e => e.Actor).HasMaxLength(80);
        builder.Property(e => e.Action).HasMaxLength(80);
        builder.Property(e => e.Entity).HasMaxLength(80);
        builder.Property(e => e.Data).HasColumnType("jsonb");
        builder.HasIndex(e => new { e.Entity, e.EntityId });
    }
}

internal sealed class AccessLogEntryConfiguration : IEntityTypeConfiguration<AccessLogEntry>
{
    public void Configure(EntityTypeBuilder<AccessLogEntry> builder)
    {
        builder.ToTable("access_logs");
        builder.Property(e => e.Ip).HasMaxLength(64);
        builder.Property(e => e.UserAgent).HasMaxLength(AccessLogEntry.MaxUserAgentLength);
        builder.Property(e => e.SessionId).HasMaxLength(64);
        builder.HasOne<User>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(e => e.OccurredAt);
        builder.HasIndex(e => new { e.UserId, e.SessionId })
            .IsUnique()
            .HasFilter("session_id IS NOT NULL");
    }
}
