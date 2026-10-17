using Microsoft.EntityFrameworkCore;
using RedigiAI.Domain.Accounts;
using RedigiAI.Domain.Billing;

namespace RedigiAI.Infrastructure.Persistence.Repositories;

internal sealed class UserRepository(AppDbContext db) : IUserRepository
{
    private static readonly TimeSpan TouchInterval = TimeSpan.FromHours(1);

    public Task<User?> FindByClerkIdAsync(string clerkUserId, CancellationToken ct) =>
        db.Users.FirstOrDefaultAsync(u => u.ClerkUserId == clerkUserId && u.DeletedAt == null, ct);

    public void Add(User user) => db.Users.Add(user);

    public Task TouchAsync(Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var threshold = now - TouchInterval;
        return db.Users
            .Where(u => u.Id == userId && u.LastActiveAt < threshold)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.LastActiveAt, now), ct);
    }
}

internal sealed class CreditRepository(AppDbContext db) : ICreditRepository
{
    public void AddGrant(CreditBucket bucket, CreditLedgerEntry entry)
    {
        db.CreditBuckets.Add(bucket);
        db.CreditLedger.Add(entry);
    }

    public void AddEntry(CreditLedgerEntry entry) => db.CreditLedger.Add(entry);

    public async Task<bool> TryDebitAsync(Guid bucketId, DateTimeOffset now, CancellationToken ct) =>
        await db.CreditBuckets
            .Where(b => b.Id == bucketId && b.Remaining >= 1 && (b.ExpiresAt == null || b.ExpiresAt > now))
            .ExecuteUpdateAsync(b => b.SetProperty(x => x.Remaining, x => x.Remaining - 1), ct) == 1;

    public async Task<CreditBucket?> FindReservedBucketAsync(Guid essayId, CancellationToken ct)
    {
        var bucketId = await db.CreditLedger
            .AsNoTracking()
            .Where(e => e.EssayId == essayId && e.Delta < 0)
            .Select(e => (Guid?)e.BucketId)
            .FirstOrDefaultAsync(ct);

        return bucketId is null ? null : await db.CreditBuckets.AsNoTracking().FirstOrDefaultAsync(b => b.Id == bucketId, ct);
    }

    public async Task<bool> TryCreditBackAsync(Guid bucketId, CancellationToken ct) =>
        await db.CreditBuckets
            .Where(b => b.Id == bucketId && b.Remaining < b.Granted)
            .ExecuteUpdateAsync(b => b.SetProperty(x => x.Remaining, x => x.Remaining + 1), ct) == 1;

    public async Task<IReadOnlyList<CreditBucket>> ListAvailableAsync(
        Guid userId, DateTimeOffset now, CancellationToken ct) =>
        await db.CreditBuckets
            .AsNoTracking()
            .Where(b => b.UserId == userId && b.Remaining > 0 && (b.ExpiresAt == null || b.ExpiresAt > now))
            .OrderBy(b => b.CreatedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlySet<Guid>> RefundedAsync(IReadOnlyCollection<Guid> essayIds, CancellationToken ct)
    {
        if (essayIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var refunded = await db.CreditLedger
            .AsNoTracking()
            .Where(e => e.Source == CreditSource.Refund && e.EssayId != null && essayIds.Contains(e.EssayId.Value))
            .Select(e => e.EssayId!.Value)
            .Distinct()
            .ToListAsync(ct);

        return refunded.ToHashSet();
    }
}
