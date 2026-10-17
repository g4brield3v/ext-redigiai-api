using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RedigiAI.Domain.Accounts;
using RedigiAI.Domain.Common;
using RedigiAI.Domain.Notifications;

namespace RedigiAI.Infrastructure.Persistence.Configurations;

internal sealed class EmailLogEntryConfiguration : IEntityTypeConfiguration<EmailLogEntry>
{
    public void Configure(EntityTypeBuilder<EmailLogEntry> builder)
    {
        builder.ToTable("email_log");
        builder.Property(e => e.Template).HasMaxLength(40);
        builder.Property(e => e.ProviderMessageId).HasMaxLength(120);
        builder.HasOne<User>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Restrict);

        // Um envio por evento (especificação, seção 10).
        builder.HasIndex(e => new { e.Template, e.RefId }).IsUnique();
        builder.HasIndex(e => e.UserId);
    }
}

internal sealed class FeatureFlagConfiguration : IEntityTypeConfiguration<FeatureFlag>
{
    public void Configure(EntityTypeBuilder<FeatureFlag> builder)
    {
        builder.ToTable("feature_flags");
        builder.HasKey(f => f.Key);
        builder.Property(f => f.Key).HasMaxLength(60);
    }
}
