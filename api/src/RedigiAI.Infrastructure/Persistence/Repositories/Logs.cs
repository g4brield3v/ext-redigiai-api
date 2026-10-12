using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Auditing;

namespace RedigiAI.Infrastructure.Persistence.Repositories;

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
