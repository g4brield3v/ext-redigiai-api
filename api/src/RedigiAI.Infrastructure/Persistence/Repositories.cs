using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Accounts;
using RedigiAI.Domain.Auditing;
using RedigiAI.Domain.Billing;

namespace RedigiAI.Infrastructure.Persistence;

internal sealed class UnitOfWork(AppDbContext db) : IUnitOfWork
{
    public async Task SaveChangesAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Nada foi gravado; solta o que ficou pendente para o caso de uso reler o estado real.
            db.ChangeTracker.Clear();
            throw new UniqueConflictException(ex);
        }
    }
}

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

internal sealed class AuditLog(AppDbContext db, TimeProvider time) : IAuditLog
{
    public void Record(string actor, string action, string entity, Guid entityId, object? data = null)
    {
        var json = data is null ? null : JsonSerializer.Serialize(data, JsonColumn.Options);
        db.AuditLog.Add(AuditLogEntry.Create(actor, action, entity, entityId, json, time.GetUtcNow()));
    }
}

internal sealed class AccessLog(AppDbContext db, TimeProvider time) : IAccessLog
{
    public void Record(Guid userId, RequestOrigin origin) =>
        db.AccessLogs.Add(Create(userId, origin));

    public async Task RecordSessionOnceAsync(Guid userId, RequestOrigin origin, CancellationToken ct)
    {
        if (origin.SessionId is null)
        {
            return;
        }

        var entry = Create(userId, origin);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO access_logs (id, user_id, ip, user_agent, session_id, occurred_at)
            VALUES ({entry.Id}, {entry.UserId}, {entry.Ip}, {entry.UserAgent}, {entry.SessionId}, {entry.OccurredAt})
            ON CONFLICT DO NOTHING
            """,
            ct);
    }

    private AccessLogEntry Create(Guid userId, RequestOrigin origin) =>
        AccessLogEntry.Create(userId, origin.Ip, origin.UserAgent, origin.SessionId, time.GetUtcNow());
}
