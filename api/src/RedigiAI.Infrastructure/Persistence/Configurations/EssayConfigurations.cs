using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RedigiAI.Domain.Accounts;
using RedigiAI.Domain.Catalog;
using RedigiAI.Domain.Essays;
using RedigiAI.Domain.Jobs;

namespace RedigiAI.Infrastructure.Persistence.Configurations;

internal sealed class EssayConfiguration : IEntityTypeConfiguration<Essay>
{
    public void Configure(EntityTypeBuilder<Essay> builder)
    {
        builder.ToTable("essays");
        builder.Property(e => e.Title).HasMaxLength(400);
        builder.Property(e => e.Status).HasMaxLength(20);
        builder.Property(e => e.FailureCode).HasMaxLength(40);
        builder.HasOne<User>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<EssayBatch>().WithMany().HasForeignKey(e => e.BatchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Exam>().WithMany().HasForeignKey(e => e.ExamId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Theme>().WithMany().HasForeignKey(e => e.ThemeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<EssayType>().WithMany().HasForeignKey(e => e.EssayTypeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<RubricVersion>().WithMany().HasForeignKey(e => e.RubricVersionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Essay>().WithMany().HasForeignKey(e => e.ParentEssayId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Asset>().WithMany().HasForeignKey(e => e.PhotoAssetId).OnDelete(DeleteBehavior.Restrict);

        // Uma foto, uma redação.
        builder.HasIndex(e => e.PhotoAssetId).IsUnique();
        // Painel: redações do aluno, da mais recente para a mais antiga (o UUID v7 já ordena por data).
        builder.HasIndex(e => new { e.UserId, e.Id });
        builder.HasIndex(e => new { e.Status, e.ReviewDeadlineAt });
    }
}

internal sealed class EssayBatchConfiguration : IEntityTypeConfiguration<EssayBatch>
{
    public void Configure(EntityTypeBuilder<EssayBatch> builder)
    {
        builder.ToTable("essay_batches");
        builder.Property(b => b.IdempotencyKey).HasMaxLength(EssayBatch.MaxKeyLength);
        builder.HasOne<User>().WithMany().HasForeignKey(b => b.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(b => new { b.UserId, b.IdempotencyKey }).IsUnique();
    }
}

internal sealed class AssetConfiguration : IEntityTypeConfiguration<Asset>
{
    public void Configure(EntityTypeBuilder<Asset> builder)
    {
        builder.ToTable("assets");
        builder.Property(a => a.Provider).HasMaxLength(20);
        builder.Property(a => a.PublicId).HasMaxLength(200);
        builder.Property(a => a.Mime).HasMaxLength(40);
        builder.Ignore(a => a.IsConfirmed);
        builder.HasOne<User>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(a => a.PublicId).IsUnique();
        builder.HasIndex(a => a.PurgeAfter).HasFilter("purge_after IS NOT NULL");
    }
}

internal sealed class TranscriptionConfiguration : IEntityTypeConfiguration<Transcription>
{
    public void Configure(EntityTypeBuilder<Transcription> builder)
    {
        builder.ToTable("transcriptions");
        builder.Property(t => t.Model).HasMaxLength(80);
        builder.Property(t => t.Lines)
            .HasColumnType("jsonb")
            .HasConversion<JsonConverter<IReadOnlyList<TranscribedLine>>, JsonComparer<IReadOnlyList<TranscribedLine>>>();
        builder.Ignore(t => t.IsUnreadable);
        builder.HasOne<Essay>().WithMany().HasForeignKey(t => t.EssayId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(t => t.Words).WithOne().HasForeignKey(w => w.TranscriptionId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(t => t.Words).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Uma transcrição por redação.
        builder.HasIndex(t => t.EssayId).IsUnique();
    }
}

internal sealed class IllegibleWordConfiguration : IEntityTypeConfiguration<IllegibleWord>
{
    public void Configure(EntityTypeBuilder<IllegibleWord> builder)
    {
        builder.ToTable("illegible_words");
        builder.Property(w => w.StudentValue).HasMaxLength(IllegibleWord.MaxValueLength);
    }
}

internal sealed class AiUsageConfiguration : IEntityTypeConfiguration<AiUsage>
{
    public void Configure(EntityTypeBuilder<AiUsage> builder)
    {
        builder.ToTable("ai_usage");
        builder.Property(u => u.Stage).HasMaxLength(20);
        builder.Property(u => u.Model).HasMaxLength(80);
        builder.HasOne<Essay>().WithMany().HasForeignKey(u => u.EssayId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(u => u.EssayId);
        builder.HasIndex(u => u.CreatedAt);
    }
}

internal sealed class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> builder)
    {
        builder.ToTable("jobs");
        builder.Property(j => j.Type).HasMaxLength(40);
        builder.Property(j => j.Payload).HasColumnType("jsonb");
        builder.Property(j => j.Status).HasMaxLength(20);
        builder.Property(j => j.LastError).HasMaxLength(2000);

        // O worker procura o próximo job pronto do tipo, pelo horário.
        builder.HasIndex(j => new { j.Type, j.RunAt }).HasFilter("status IN ('ready', 'running')");
    }
}
