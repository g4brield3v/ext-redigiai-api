using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RedigiAI.Domain.Catalog;
using RedigiAI.Domain.Essays;
using RedigiAI.Domain.Grading;

namespace RedigiAI.Infrastructure.Persistence.Configurations;

internal sealed class PromptTemplateConfiguration : IEntityTypeConfiguration<PromptTemplate>
{
    public void Configure(EntityTypeBuilder<PromptTemplate> builder)
    {
        builder.ToTable("prompt_templates");
        builder.Property(p => p.Kind).HasMaxLength(20);
        builder.HasIndex(p => new { p.Kind, p.Version }).IsUnique();
    }
}

internal sealed class GuidelinesVersionConfiguration : IEntityTypeConfiguration<GuidelinesVersion>
{
    public void Configure(EntityTypeBuilder<GuidelinesVersion> builder)
    {
        builder.ToTable("rubric_guideline_versions");
        builder.HasOne<Exam>().WithMany().HasForeignKey(g => g.ExamId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(g => g.Criteria).WithOne().HasForeignKey(c => c.GuidelinesVersionId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(g => g.Criteria).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasIndex(g => new { g.ExamId, g.Version }).IsUnique();
    }
}

internal sealed class RubricGuidelineConfiguration : IEntityTypeConfiguration<RubricGuideline>
{
    public void Configure(EntityTypeBuilder<RubricGuideline> builder)
    {
        builder.ToTable("rubric_guidelines");
        builder.HasIndex(g => new { g.GuidelinesVersionId, g.CriterionPosition }).IsUnique();
    }
}

internal sealed class WritingRulesVersionConfiguration : IEntityTypeConfiguration<WritingRulesVersion>
{
    public void Configure(EntityTypeBuilder<WritingRulesVersion> builder)
    {
        builder.ToTable("writing_rule_versions");
        builder.HasMany(v => v.Rules).WithOne().HasForeignKey(r => r.WritingRulesVersionId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(v => v.Rules).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasIndex(v => v.Version).IsUnique();
    }
}

internal sealed class WritingRuleConfiguration : IEntityTypeConfiguration<WritingRule>
{
    public void Configure(EntityTypeBuilder<WritingRule> builder)
    {
        builder.ToTable("writing_rules");
        builder.Property(r => r.Code).HasMaxLength(40);
        builder.Property(r => r.Name).HasMaxLength(120);
        builder.Property(r => r.Explanation).HasMaxLength(400);
        builder.Property(r => r.WrongExample).HasMaxLength(200);
        builder.Property(r => r.RightExample).HasMaxLength(200);
        builder.HasIndex(r => new { r.WritingRulesVersionId, r.Code }).IsUnique();
    }
}

internal sealed class GradingProfileConfiguration : IEntityTypeConfiguration<GradingProfile>
{
    public void Configure(EntityTypeBuilder<GradingProfile> builder)
    {
        builder.ToTable("grading_profiles");
        builder.Property(p => p.Status).HasMaxLength(20);
        builder.Property(p => p.TranscriptionModel).HasMaxLength(80);
        builder.Property(p => p.GradingModel).HasMaxLength(80);
        builder.Property(p => p.TranscriptionPromptId).HasColumnName("transcription_prompt_template_id");
        builder.Property(p => p.GradingPromptId).HasColumnName("grading_prompt_template_id");
        builder.HasOne<Exam>().WithMany().HasForeignKey(p => p.ExamId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<EssayType>().WithMany().HasForeignKey(p => p.EssayTypeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PromptTemplate>().WithMany().HasForeignKey(p => p.TranscriptionPromptId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PromptTemplate>().WithMany().HasForeignKey(p => p.GradingPromptId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<RubricVersion>().WithMany().HasForeignKey(p => p.RubricVersionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<GuidelinesVersion>().WithMany().HasForeignKey(p => p.GuidelinesVersionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<WritingRulesVersion>().WithMany().HasForeignKey(p => p.WritingRulesVersionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AnchorSet>().WithMany().HasForeignKey(p => p.AnchorSetId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(p => new { p.ExamId, p.EssayTypeId, p.Version }).IsUnique();

        // O banco impede dois perfis ativos para o mesmo vestibular e tipo.
        builder.HasIndex(p => new { p.ExamId, p.EssayTypeId }, "ux_grading_profiles_active")
            .HasDatabaseName("ux_grading_profiles_active")
            .IsUnique()
            .HasFilter("status = 'active'");
    }
}

// Calibração: tabelas vazias até haver redações avaliadas por professor.

internal sealed class AnchorSetConfiguration : IEntityTypeConfiguration<AnchorSet>
{
    public void Configure(EntityTypeBuilder<AnchorSet> builder)
    {
        builder.ToTable("anchor_sets");
        builder.HasOne<Exam>().WithMany().HasForeignKey(a => a.ExamId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<EssayType>().WithMany().HasForeignKey(a => a.EssayTypeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(a => new { a.ExamId, a.EssayTypeId, a.Version }).IsUnique();
    }
}

internal sealed class AnchorEssayConfiguration : IEntityTypeConfiguration<AnchorEssay>
{
    public void Configure(EntityTypeBuilder<AnchorEssay> builder)
    {
        builder.ToTable("anchor_essays");
        builder.Property(a => a.Band).HasMaxLength(40);
        builder.Property(a => a.Lines).HasColumnType("jsonb");
        builder.HasOne<AnchorSet>().WithMany().HasForeignKey(a => a.AnchorSetId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AnchorScoreConfiguration : IEntityTypeConfiguration<AnchorScore>
{
    public void Configure(EntityTypeBuilder<AnchorScore> builder)
    {
        builder.ToTable("anchor_scores");
        builder.HasOne<AnchorEssay>().WithMany().HasForeignKey(s => s.AnchorEssayId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(s => new { s.AnchorEssayId, s.CriterionPosition }).IsUnique();
    }
}

internal sealed class ReferenceEssayConfiguration : IEntityTypeConfiguration<ReferenceEssay>
{
    public void Configure(EntityTypeBuilder<ReferenceEssay> builder)
    {
        builder.ToTable("reference_essays");
        builder.Property(r => r.Source).HasMaxLength(200);
        builder.Property(r => r.Lines).HasColumnType("jsonb");
        builder.Property(r => r.Levels).HasColumnType("jsonb");
        builder.HasOne<Exam>().WithMany().HasForeignKey(r => r.ExamId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<EssayType>().WithMany().HasForeignKey(r => r.EssayTypeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Prompt>().WithMany().HasForeignKey(r => r.PromptId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class EvalRunConfiguration : IEntityTypeConfiguration<EvalRun>
{
    public void Configure(EntityTypeBuilder<EvalRun> builder)
    {
        builder.ToTable("eval_runs");
        builder.Property(r => r.Results).HasColumnType("jsonb");
        builder.HasOne<GradingProfile>().WithMany().HasForeignKey(r => r.GradingProfileId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CorrectionConfiguration : IEntityTypeConfiguration<Correction>
{
    public void Configure(EntityTypeBuilder<Correction> builder)
    {
        builder.ToTable("corrections");
        builder.Property(c => c.ZeroReason).HasMaxLength(60);
        builder.Property(c => c.Model).HasMaxLength(80);
        builder.Property(c => c.RawResponse).HasColumnType("jsonb");
        builder.Property(c => c.NextSteps)
            .HasColumnType("jsonb")
            .HasConversion<JsonConverter<IReadOnlyList<NextStep>>, JsonComparer<IReadOnlyList<NextStep>>>();
        builder.HasOne<Essay>().WithMany().HasForeignKey(c => c.EssayId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<RubricVersion>().WithMany().HasForeignKey(c => c.RubricVersionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<GradingProfile>().WithMany().HasForeignKey(c => c.GradingProfileId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Prompt>().WithMany().HasForeignKey(c => c.PromptId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PromptGradingNotes>().WithMany().HasForeignKey(c => c.PromptGradingNotesId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(c => c.Criteria).WithOne().HasForeignKey(x => x.CorrectionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(c => c.Marks).WithOne().HasForeignKey(x => x.CorrectionId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(c => c.Criteria).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(c => c.Marks).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Uma correção por redação: é o que impede duas execuções do mesmo job de gravarem duas.
        builder.HasIndex(c => c.EssayId).IsUnique();
    }
}

internal sealed class CorrectionCriterionConfiguration : IEntityTypeConfiguration<CorrectionCriterion>
{
    public void Configure(EntityTypeBuilder<CorrectionCriterion> builder)
    {
        builder.ToTable("correction_criteria");
        builder.HasOne<RubricCriterion>().WithMany().HasForeignKey(c => c.CriterionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(c => new { c.CorrectionId, c.Position }).IsUnique();
    }
}

internal sealed class CorrectionMarkConfiguration : IEntityTypeConfiguration<CorrectionMark>
{
    public void Configure(EntityTypeBuilder<CorrectionMark> builder)
    {
        builder.ToTable("correction_marks");
        builder.Property(m => m.Kind).HasMaxLength(10);
        builder.Property(m => m.RuleCode).HasMaxLength(40);
        builder.HasOne<RubricCriterion>().WithMany().HasForeignKey(m => m.CriterionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(m => m.CorrectionId);
    }
}

internal sealed class CorrectionFeedbackConfiguration : IEntityTypeConfiguration<CorrectionFeedback>
{
    public void Configure(EntityTypeBuilder<CorrectionFeedback> builder)
    {
        builder.ToTable("correction_feedback");
        builder.Property(f => f.Rating).HasMaxLength(20);
        builder.Property(f => f.Comment).HasMaxLength(CorrectionFeedback.MaxCommentLength);
        builder.HasOne<Correction>().WithMany().HasForeignKey(f => f.CorrectionId).OnDelete(DeleteBehavior.Cascade);

        // Uma avaliação por correção: é o que impede dois envios simultâneos de gravarem duas.
        builder.HasIndex(f => f.CorrectionId).IsUnique();
    }
}
