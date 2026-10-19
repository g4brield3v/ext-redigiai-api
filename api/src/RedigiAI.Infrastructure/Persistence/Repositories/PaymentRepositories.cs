using Microsoft.EntityFrameworkCore;
using RedigiAI.Domain.Billing;

namespace RedigiAI.Infrastructure.Persistence.Repositories;

internal sealed class PaymentRepository(AppDbContext db) : IPaymentRepository
{
    public void Add(Payment payment) => db.Payments.Add(payment);

    public Task<Payment?> FindAsync(Guid paymentId, CancellationToken ct) =>
        db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == paymentId, ct);

    public Task<Payment?> FindByKeyAsync(Guid userId, string idempotencyKey, CancellationToken ct) =>
        db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId && p.IdempotencyKey == idempotencyKey, ct);

    public async Task<IReadOnlyList<Payment>> ListByUserAsync(Guid userId, int limit, CancellationToken ct) =>
        await db.Payments
            .AsNoTracking()
            .Where(p => p.UserId == userId && p.MpPaymentId != null)
            .OrderByDescending(p => p.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);

    public async Task<bool> TryAttachProviderAsync(
        Guid paymentId, string mpPaymentId, string? pixQrCode, string? pixQrCodeBase64, DateTimeOffset? pixExpiresAt,
        DateTimeOffset now, CancellationToken ct) =>
        await db.Payments
            .Where(p => p.Id == paymentId && (p.MpPaymentId == null || p.MpPaymentId == mpPaymentId))
            .ExecuteUpdateAsync(
                p => p
                    .SetProperty(x => x.MpPaymentId, mpPaymentId)
                    .SetProperty(x => x.PixQrCode, x => pixQrCode ?? x.PixQrCode)
                    .SetProperty(x => x.PixQrCodeBase64, x => pixQrCodeBase64 ?? x.PixQrCodeBase64)
                    .SetProperty(x => x.PixExpiresAt, x => pixExpiresAt ?? x.PixExpiresAt)
                    .SetProperty(x => x.UpdatedAt, now),
                ct) == 1;

    public async Task<bool> TryTransitionAsync(Guid paymentId, PaymentStatus to, DateTimeOffset now, CancellationToken ct)
    {
        var from = Payment.ReachableFrom(to);
        if (from.Count == 0)
        {
            return false;
        }

        return await db.Payments
            .Where(p => p.Id == paymentId && from.Contains(p.Status))
            .ExecuteUpdateAsync(
                p => p
                    .SetProperty(x => x.Status, to)
                    .SetProperty(x => x.ApprovedAt, x => to == PaymentStatus.Approved ? now : x.ApprovedAt)
                    .SetProperty(x => x.UpdatedAt, now),
                ct) == 1;
    }
}

internal sealed class WebhookEventRepository(AppDbContext db) : IWebhookEventRepository
{
    public async Task<bool> TryAddAsync(WebhookEvent webhookEvent, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO webhook_events (id, provider, external_id, topic, resource_id, payload, received_at)
            VALUES ({webhookEvent.Id}, {webhookEvent.Provider}, {webhookEvent.ExternalId}, {webhookEvent.Topic},
                    {webhookEvent.ResourceId}, {webhookEvent.Payload}, {webhookEvent.ReceivedAt})
            ON CONFLICT (provider, external_id) DO NOTHING
            """,
            ct) == 1;

    public Task MarkProcessedAsync(Guid eventId, DateTimeOffset now, CancellationToken ct) =>
        db.WebhookEvents
            .Where(e => e.Id == eventId && e.ProcessedAt == null)
            .ExecuteUpdateAsync(e => e.SetProperty(x => x.ProcessedAt, now), ct);
}
