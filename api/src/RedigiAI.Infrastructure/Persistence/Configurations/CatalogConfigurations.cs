using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RedigiAI.Domain.Catalog;

namespace RedigiAI.Infrastructure.Persistence.Configurations;

internal sealed class ExamConfiguration : IEntityTypeConfiguration<Exam>
{
    public void Configure(EntityTypeBuilder<Exam> builder)
    {
        builder.ToTable("exams");
        builder.Property(e => e.Slug).HasMaxLength(40);
        builder.Property(e => e.Name).HasMaxLength(80);
        builder.Property(e => e.Status).HasMaxLength(20);
        builder.HasIndex(e => e.Slug).IsUnique();
    }
}

internal sealed class EssayTypeConfiguration : IEntityTypeConfiguration<EssayType>
{
    public void Configure(EntityTypeBuilder<EssayType> builder)
    {
        builder.ToTable("essay_types");
        builder.Property(e => e.Slug).HasMaxLength(40);
        builder.Property(e => e.Name).HasMaxLength(80);
        builder.Property(e => e.Description).HasMaxLength(200);
        builder.HasOne<Exam>().WithMany().HasForeignKey(e => e.ExamId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(e => new { e.ExamId, e.Slug }).IsUnique();
    }
}

internal sealed class RubricVersionConfiguration : IEntityTypeConfiguration<RubricVersion>
{
    public void Configure(EntityTypeBuilder<RubricVersion> builder)
    {
        builder.ToTable("rubric_versions");
        builder.Property(r => r.Source).HasMaxLength(200);
        builder.Property(r => r.Label).HasMaxLength(80);
        builder.Property(r => r.ZeroRules)
            .HasColumnType("jsonb")
            .HasConversion<JsonConverter<IReadOnlyList<ZeroRule>>, JsonComparer<IReadOnlyList<ZeroRule>>>();
        builder.HasOne<Exam>().WithMany().HasForeignKey(r => r.ExamId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(r => r.Criteria).WithOne().HasForeignKey(c => c.RubricVersionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(r => new { r.ExamId, r.Version }).IsUnique();
    }
}

internal sealed class RubricCriterionConfiguration : IEntityTypeConfiguration<RubricCriterion>
{
    public void Configure(EntityTypeBuilder<RubricCriterion> builder)
    {
        builder.ToTable("rubric_criteria");
        builder.Property(c => c.Name).HasMaxLength(120);
        builder.Property(c => c.ShortName).HasMaxLength(120);
        builder.Property(c => c.LevelDescriptors)
            .HasColumnType("jsonb")
            .HasConversion<JsonConverter<IReadOnlyDictionary<int, string>>, JsonComparer<IReadOnlyDictionary<int, string>>>();
        builder.HasIndex(c => new { c.RubricVersionId, c.Position }).IsUnique();
    }
}

internal sealed class ThemeConfiguration : IEntityTypeConfiguration<Theme>
{
    public void Configure(EntityTypeBuilder<Theme> builder)
    {
        builder.ToTable("themes");
        builder.Property(t => t.Title).HasMaxLength(200);
        builder.Property(t => t.Status).HasMaxLength(20);
        builder.HasOne<Exam>().WithMany().HasForeignKey(t => t.ExamId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(t => t.Texts).WithOne().HasForeignKey(x => x.ThemeId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(t => t.AcceptedTypes).WithOne().HasForeignKey(x => x.ThemeId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Theme>().WithMany().HasForeignKey(t => t.SupersedesThemeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(t => new { t.ExamId, t.Status });
        // Cada versão publicada tem no máximo uma revisão.
        builder.HasIndex(t => t.SupersedesThemeId).IsUnique();
    }
}

internal sealed class ThemeTextConfiguration : IEntityTypeConfiguration<ThemeText>
{
    public void Configure(EntityTypeBuilder<ThemeText> builder)
    {
        builder.ToTable("theme_texts");
        builder.Property(t => t.Kind).HasMaxLength(20);
        builder.Property(t => t.Title).HasMaxLength(200);
        builder.Property(t => t.Source).HasMaxLength(300);
        builder.HasIndex(t => new { t.ThemeId, t.Position }).IsUnique();
    }
}

internal sealed class ThemeEssayTypeConfiguration : IEntityTypeConfiguration<ThemeEssayType>
{
    public void Configure(EntityTypeBuilder<ThemeEssayType> builder)
    {
        builder.ToTable("theme_essay_types");
        builder.HasKey(t => new { t.ThemeId, t.EssayTypeId });
        builder.Property(t => t.Instruction).HasMaxLength(2000).HasDefaultValue("");
        builder.HasOne<EssayType>().WithMany().HasForeignKey(t => t.EssayTypeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<RubricVersion>().WithMany().HasForeignKey(t => t.RubricVersionId).OnDelete(DeleteBehavior.Restrict);
    }
}
