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
