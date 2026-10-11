using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Essays;
using RedigiAI.Domain.Jobs;

namespace RedigiAI.Infrastructure.Persistence.Repositories;

internal sealed class EssayRepository(AppDbContext db) : IEssayRepository
{
    public void Add(Essay essay) => db.Essays.Add(essay);

    public Task<Essay?> FindAsync(Guid essayId, Guid userId, CancellationToken ct) =>
        db.Essays.AsNoTracking().FirstOrDefaultAsync(e => e.Id == essayId && e.UserId == userId, ct);

    public async Task<IReadOnlyList<Essay>> ListByUserAsync(
        Guid userId, EssayStatus? status, Guid? before, int limit, CancellationToken ct)
    {
        var essays = db.Essays.AsNoTracking().Where(e => e.UserId == userId);
        if (status is { } wanted)
        {
            essays = essays.Where(e => e.Status == wanted);
        }

        if (before is { } cursor)
        {
            essays = essays.Where(e => e.Id < cursor);
        }

        return await essays.OrderByDescending(e => e.Id).Take(limit).ToListAsync(ct);
    }

    public async Task<IReadOnlySet<Guid>> RewrittenAsync(IReadOnlyCollection<Guid> essayIds, CancellationToken ct)
    {
        if (essayIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var parents = await db.Essays
            .AsNoTracking()
            .Where(e => e.ParentEssayId != null && essayIds.Contains(e.ParentEssayId.Value))
            .Select(e => e.ParentEssayId!.Value)
            .Distinct()
            .ToListAsync(ct);

        return parents.ToHashSet();
    }
}

internal sealed class EssayBatchRepository(AppDbContext db) : IEssayBatchRepository
{
    public void Add(EssayBatch batch) => db.EssayBatches.Add(batch);

    public async Task<IReadOnlyList<Guid>?> FindEssayIdsAsync(Guid userId, string idempotencyKey, CancellationToken ct)
    {
        var batchId = await db.EssayBatches
            .AsNoTracking()
            .Where(b => b.UserId == userId && b.IdempotencyKey == idempotencyKey)
            .Select(b => (Guid?)b.Id)
            .FirstOrDefaultAsync(ct);
        if (batchId is null)
        {
            return null;
        }

        return await db.Essays.AsNoTracking().Where(e => e.BatchId == batchId).OrderBy(e => e.Id).Select(e => e.Id).ToListAsync(ct);
    }
}

internal sealed class AssetRepository(AppDbContext db) : IAssetRepository
{
    public void Add(Asset asset) => db.Assets.Add(asset);

    public Task<Asset?> FindAsync(Guid assetId, Guid userId, CancellationToken ct) =>
        db.Assets.FirstOrDefaultAsync(a => a.Id == assetId && a.UserId == userId, ct);
}

internal sealed class JobQueue(AppDbContext db, TimeProvider time) : IJobQueue
{
    public void Enqueue(string type, object payload, int maxAttempts) =>
        db.Jobs.Add(Job.Create(type, JsonSerializer.Serialize(payload, JsonColumn.Options), maxAttempts, time.GetUtcNow()));
}
