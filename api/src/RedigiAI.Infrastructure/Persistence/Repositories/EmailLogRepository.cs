using Microsoft.EntityFrameworkCore;
using RedigiAI.Domain.Notifications;

namespace RedigiAI.Infrastructure.Persistence.Repositories;

internal sealed class EmailLogRepository(AppDbContext db) : IEmailLogRepository
{
    public async Task<Guid?> TryClaimAsync(EmailLogEntry entry, DateTimeOffset staleBefore, CancellationToken ct)
    {
        // Um comando só: cria a linha ou, se ela existe sem envio e sem reserva recente, retoma a reserva. Com envio
        // feito ou reserva recente de outro processo, nada muda e nenhuma linha volta.
        var claimed = await db.Database
            .SqlQuery<Guid>(
                $"""
                INSERT INTO email_log (id, user_id, template, ref_id, claimed_at, created_at)
                VALUES ({entry.Id}, {entry.UserId}, {entry.Template}, {entry.RefId}, {entry.ClaimedAt}, {entry.CreatedAt})
                ON CONFLICT (template, ref_id) DO UPDATE SET claimed_at = EXCLUDED.claimed_at
                WHERE email_log.sent_at IS NULL
                  AND (email_log.claimed_at IS NULL OR email_log.claimed_at < {staleBefore})
                RETURNING id AS "Value"
                """)
            .ToListAsync(ct);

        return claimed.Count == 1 ? claimed[0] : null;
    }

    public Task MarkSentAsync(Guid id, string? providerMessageId, DateTimeOffset now, CancellationToken ct) =>
        db.EmailLog
            .Where(e => e.Id == id)
            .ExecuteUpdateAsync(
                entry => entry.SetProperty(e => e.SentAt, now).SetProperty(e => e.ProviderMessageId, providerMessageId), ct);

    public Task ReleaseAsync(Guid id, CancellationToken ct) =>
        db.EmailLog
            .Where(e => e.Id == id && e.SentAt == null)
            .ExecuteUpdateAsync(entry => entry.SetProperty(e => e.ClaimedAt, (DateTimeOffset?)null), ct);
}
