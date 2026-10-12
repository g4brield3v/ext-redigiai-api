using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RedigiAI.Domain.Accounts;
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

internal sealed class PromptConfiguration : IEntityTypeConfiguration<Prompt>
{
    public void Configure(EntityTypeBuilder<Prompt> builder)
    {
        builder.ToTable("prompts");
        builder.Property(p => p.Slug).HasMaxLength(PromptRules.MaxSlugLength);
        builder.Property(p => p.Status).HasMaxLength(20);
        builder.Property(p => p.Title).HasMaxLength(PromptRules.MaxTitleLength);
        builder.Property(p => p.Summary).HasMaxLength(PromptRules.MaxSummaryLength);
        builder.Property(p => p.SourceLabel).HasMaxLength(PromptRules.MaxSourceLabelLength);
        builder.Property(p => p.GenreLabel).HasMaxLength(PromptRules.MaxGenreLength);
        builder.Property(p => p.CreationKey).HasMaxLength(Prompt.MaxCreationKeyLength);
        builder.HasIndex(p => new { p.CreatedBy, p.CreationKey }).IsUnique();
        builder.HasOne<Exam>().WithMany().HasForeignKey(p => p.ExamId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<EssayType>().WithMany().HasForeignKey(p => p.EssayTypeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(p => p.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(p => p.PublishedBy).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(p => p.Blocks).WithOne().HasForeignKey(b => b.PromptId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(p => p.GradingNotes).WithOne().HasForeignKey<PromptGradingNotes>(n => n.PromptId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(p => new { p.Slug, p.Version }).IsUnique();
        // Só uma versão publicada por endereço, garantido pelo banco.
        builder.HasIndex(p => p.Slug, "ux_prompts_published_per_slug")
            .HasDatabaseName("ux_prompts_published_per_slug")
            .IsUnique()
            .HasFilter("status = 'published'");
        builder.HasIndex(p => new { p.ExamId, p.Status });
    }
}

internal sealed class PromptBlockConfiguration : IEntityTypeConfiguration<PromptBlock>
{
    public void Configure(EntityTypeBuilder<PromptBlock> builder)
    {
        // A posição é única por proposta por uma restrição adiável (ux_prompt_blocks_position), criada na migração:
        // assim a troca de posição entre dois blocos vale no fim da transação, e não a cada linha gravada.
        builder.ToTable("prompt_blocks", table => table.HasCheckConstraint("ck_prompt_blocks_position", "position >= 0"));
        builder.Property(b => b.Kind).HasMaxLength(20);
        builder.Property(b => b.Data)
            .HasColumnType("jsonb")
            .HasConversion<JsonConverter<PromptBlockData>, JsonComparer<PromptBlockData>>();
    }
}

internal sealed class PromptGradingNotesConfiguration : IEntityTypeConfiguration<PromptGradingNotes>
{
    public void Configure(EntityTypeBuilder<PromptGradingNotes> builder)
    {
        builder.ToTable("prompt_grading_notes");
        builder.Property(n => n.KeyData)
            .HasColumnType("jsonb")
            .HasConversion<JsonConverter<IReadOnlyList<string>>, JsonComparer<IReadOnlyList<string>>>();
    }
}

