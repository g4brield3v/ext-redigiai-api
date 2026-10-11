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

    public async Task<IReadOnlyList<CreditBucket>> ListAvailableAsync(
        Guid userId, DateTimeOffset now, CancellationToken ct) =>
        await db.CreditBuckets
            .AsNoTracking()
            .Where(b => b.UserId == userId && b.Remaining > 0 && (b.ExpiresAt == null || b.ExpiresAt > now))
            .OrderBy(b => b.CreatedAt)
            .ToListAsync(ct);
}
