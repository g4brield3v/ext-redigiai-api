using Microsoft.EntityFrameworkCore;
using RedigiAI.Domain.Accounts;
using RedigiAI.Domain.Billing;

namespace RedigiAI.Infrastructure.Persistence.Repositories;

internal sealed class UserRepository(AppDbContext db) : IUserRepository
{
    private static readonly TimeSpan TouchInterval = TimeSpan.FromHours(1);

    public Task<User?> FindByClerkIdAsync(string clerkUserId, CancellationToken ct) =>
        db.Users.FirstOrDefaultAsync(u => u.ClerkUserId == clerkUserId && u.DeletedAt == null, ct);

    public Task<User?> FindActiveByIdAsync(Guid userId, CancellationToken ct) =>
        db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId && u.DeletedAt == null, ct);

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

    public async Task<bool> TryAddKeyedGrantAsync(CreditBucket bucket, CreditLedgerEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bucket.GrantKey);
        var source = SnakeCaseEnum<CreditSource>.ToName(bucket.Source);
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO credit_buckets (id, user_id, source, granted, remaining, expires_at, created_at, grant_key)
            VALUES ({bucket.Id}, {bucket.UserId}, {source}, {bucket.Granted}, {bucket.Remaining}, {bucket.ExpiresAt},
                    {bucket.CreatedAt}, {bucket.GrantKey})
            ON CONFLICT (grant_key) WHERE grant_key IS NOT NULL DO NOTHING
            """,
            ct);
        if (inserted == 0)
        {
            return false;
        }

        db.CreditLedger.Add(entry);
        return true;
    }

    public async Task<IReadOnlyList<CreditBucket>> ListExpiredWithBalanceAsync(
        DateTimeOffset now, int limit, CancellationToken ct) =>
        await db.CreditBuckets
            .AsNoTracking()
            .Where(b => b.Remaining > 0 && b.ExpiresAt != null && b.ExpiresAt <= now)
            .OrderBy(b => b.ExpiresAt)
            .Take(limit)
            .ToListAsync(ct);

    public async Task<int> TryExpireAsync(Guid bucketId, DateTimeOffset now, CancellationToken ct)
    {
        // A linha fica travada até o fim da transação: o saldo zerado e o lançamento negativo valem juntos.
        var expired = await db.Database
            .SqlQuery<int>(
                $"""
                WITH current AS (
                    SELECT id, remaining FROM credit_buckets
                    WHERE id = {bucketId} AND remaining > 0 AND expires_at IS NOT NULL AND expires_at <= {now}
                    FOR UPDATE
                )
                UPDATE credit_buckets SET remaining = 0
                FROM current
                WHERE credit_buckets.id = current.id
                RETURNING current.remaining AS "Value"
                """)
            .ToListAsync(ct);
        return expired.Count == 1 ? expired[0] : 0;
    }

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

internal sealed class SubscriptionRepository(AppDbContext db) : ISubscriptionRepository
{
    public void Add(Subscription subscription) => db.Subscriptions.Add(subscription);

    public async Task<IReadOnlyList<Subscription>> ListEarningWeeklyCreditsAsync(
        DateTimeOffset weekStart, CancellationToken ct) =>
        await db.Subscriptions
            .AsNoTracking()
            .Where(s => s.Status == SubscriptionStatus.Active && s.CreatedAt < weekStart)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(ct);

    public Task<Subscription?> FindLatestAsync(Guid userId, CancellationToken ct) =>
        db.Subscriptions
            .AsNoTracking()
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);
}
