using Microsoft.EntityFrameworkCore;
using RedigiAI.Domain.Billing;

namespace RedigiAI.Infrastructure.Persistence.Repositories;

internal sealed class PaymentRepository(AppDbContext db) : IPaymentRepository
{
    public void Add(Payment payment) => db.Payments.Add(payment);

    public Task<Payment?> FindAsync(Guid paymentId, CancellationToken ct) =>
        db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == paymentId, ct);

    public Task<Payment?> FindByProviderIdAsync(string mpPaymentId, CancellationToken ct) =>
        db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.MpPaymentId == mpPaymentId, ct);

    public Task<Payment?> FindByKeyAsync(Guid userId, string idempotencyKey, CancellationToken ct) =>
        db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId && p.IdempotencyKey == idempotencyKey, ct);

    public async Task<IReadOnlyList<Payment>> ListByUserAsync(Guid userId, int limit, CancellationToken ct) =>
        await db.Payments
            .AsNoTracking()
            .Where(p => p.UserId == userId && p.MpPaymentId != null)
            .OrderByDescending(p => p.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<string>> ListPendingToRecheckAsync(
        DateTimeOffset createdAfter, DateTimeOffset createdBefore, int limit, CancellationToken ct)
    {
        var pending = SnakeCaseEnum<PaymentStatus>.ToName(PaymentStatus.Pending);
        var type = RedigiAI.Domain.Jobs.JobTypes.ProcessPayment;
        var ready = SnakeCaseEnum<RedigiAI.Domain.Jobs.JobStatus>.ToName(RedigiAI.Domain.Jobs.JobStatus.Ready);
        var running = SnakeCaseEnum<RedigiAI.Domain.Jobs.JobStatus>.ToName(RedigiAI.Domain.Jobs.JobStatus.Running);
        return await db.Database
            .SqlQuery<string>(
                $"""
                SELECT p.mp_payment_id AS "Value" FROM payments p
                WHERE p.status = {pending} AND p.mp_payment_id IS NOT NULL
                  AND p.created_at > {createdAfter} AND p.created_at <= {createdBefore}
                  AND NOT EXISTS (
                      SELECT 1 FROM jobs j
                      WHERE j.type = {type} AND j.status IN ({ready}, {running})
                        AND j.payload ->> 'mpPaymentId' = p.mp_payment_id)
                ORDER BY p.created_at
                LIMIT {limit}
                """)
            .ToListAsync(ct);
    }

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

    public async Task<bool> TryRecordRefundAsync(
        Guid paymentId, long fromCents, long toCents, DateTimeOffset now, CancellationToken ct) =>
        await db.Payments
            .Where(p => p.Id == paymentId && p.Status == PaymentStatus.Approved && p.RefundedCents == fromCents && toCents > fromCents)
            .ExecuteUpdateAsync(p => p.SetProperty(x => x.RefundedCents, toCents).SetProperty(x => x.UpdatedAt, now), ct) == 1;

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

internal sealed class SubscriptionRepository(AppDbContext db) : ISubscriptionRepository
{
    public void Add(Subscription subscription) => db.Subscriptions.Add(subscription);

    public Task<Subscription?> FindAsync(Guid subscriptionId, CancellationToken ct) =>
        db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == subscriptionId, ct);

    public Task<Subscription?> FindByKeyAsync(Guid userId, string idempotencyKey, CancellationToken ct) =>
        db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == userId && s.IdempotencyKey == idempotencyKey, ct);

    public Task<Subscription?> FindByProviderIdAsync(string mpPreapprovalId, CancellationToken ct) =>
        db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.MpPreapprovalId == mpPreapprovalId, ct);

    public Task<Subscription?> FindPendingAsync(Guid userId, CancellationToken ct) =>
        db.Subscriptions
            .AsNoTracking()
            .Where(s => s.UserId == userId && s.Status == SubscriptionStatus.Pending)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);

    public Task<Subscription?> FindOpenAsync(Guid userId, CancellationToken ct) =>
        db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(
            s => s.UserId == userId && (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Paused), ct);

    public Task<Subscription?> FindLatestAsync(Guid userId, CancellationToken ct) =>
        db.Subscriptions
            .AsNoTracking()
            .Where(s => s.UserId == userId && (s.Status != SubscriptionStatus.Pending || s.MpPreapprovalId != null))
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);

    public async Task<bool> TryAttachProviderAsync(
        Guid subscriptionId, string mpPreapprovalId, DateTimeOffset now, CancellationToken ct, DateTimeOffset? firstChargeAt = null) =>
        await db.Subscriptions
            .Where(s => s.Id == subscriptionId && (s.MpPreapprovalId == null || s.MpPreapprovalId == mpPreapprovalId))
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(x => x.MpPreapprovalId, mpPreapprovalId)
                    .SetProperty(x => x.CreatingUntil, (DateTimeOffset?)null)
                    .SetProperty(x => x.FirstChargeAt, x => firstChargeAt ?? x.FirstChargeAt)
                    .SetProperty(x => x.UpdatedAt, now),
                ct) == 1;

    public async Task<DateTimeOffset?> TryBeginCreationAsync(Guid subscriptionId, DateTimeOffset now, CancellationToken ct)
    {
        var until = now + Subscription.CreationLock;
        var locked = await db.Subscriptions
            .Where(s => s.Id == subscriptionId && s.Status == SubscriptionStatus.Pending && s.MpPreapprovalId == null
                && (s.CreatingUntil == null || s.CreatingUntil <= now))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatingUntil, until), ct) == 1;
        // O valor como o banco o guarda (microssegundos), para conferir o dono ao soltar.
        return locked ? await db.Subscriptions.Where(s => s.Id == subscriptionId).Select(s => s.CreatingUntil).SingleAsync(ct) : null;
    }

    public Task EndCreationAsync(Guid subscriptionId, DateTimeOffset lockedUntil, CancellationToken ct) =>
        db.Subscriptions
            .Where(s => s.Id == subscriptionId && s.CreatingUntil == lockedUntil)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatingUntil, (DateTimeOffset?)null), ct);

    public async Task<bool> TryTransitionAsync(Guid subscriptionId, SubscriptionStatus to, DateTimeOffset now, CancellationToken ct)
    {
        var from = Subscription.ReachableFrom(to);
        if (from.Count == 0)
        {
            return false;
        }

        try
        {
            return await db.Subscriptions
                .Where(s => s.Id == subscriptionId && from.Contains(s.Status))
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(x => x.Status, to)
                        .SetProperty(x => x.CancelledAt, x => to == SubscriptionStatus.Cancelled ? now : x.CancelledAt)
                        .SetProperty(x => x.UpdatedAt, now),
                    ct) == 1;
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            throw new RedigiAI.Application.Abstractions.UniqueConflictException(ex);
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation)
        {
            throw new RedigiAI.Application.Abstractions.UniqueConflictException(ex);
        }
    }

    public Task UpdateBillingAsync(
        Guid subscriptionId, DateTimeOffset? nextBillingAt, string? cardLast4, DateTimeOffset now, CancellationToken ct) =>
        db.Subscriptions
            .Where(s => s.Id == subscriptionId)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(x => x.NextBillingAt, x => nextBillingAt ?? x.NextBillingAt)
                    .SetProperty(x => x.CardLast4, x => cardLast4 ?? x.CardLast4)
                    .SetProperty(x => x.UpdatedAt, now),
                ct);

    public async Task<IReadOnlyList<string>> ListPendingToRecheckAsync(
        DateTimeOffset createdAfter, DateTimeOffset createdBefore, int limit, CancellationToken ct)
    {
        var pending = SnakeCaseEnum<SubscriptionStatus>.ToName(SubscriptionStatus.Pending);
        var type = RedigiAI.Domain.Jobs.JobTypes.ProcessSubscription;
        var ready = SnakeCaseEnum<RedigiAI.Domain.Jobs.JobStatus>.ToName(RedigiAI.Domain.Jobs.JobStatus.Ready);
        var running = SnakeCaseEnum<RedigiAI.Domain.Jobs.JobStatus>.ToName(RedigiAI.Domain.Jobs.JobStatus.Running);
        return await db.Database
            .SqlQuery<string>(
                $"""
                SELECT s.mp_preapproval_id AS "Value" FROM subscriptions s
                WHERE s.status = {pending} AND s.mp_preapproval_id IS NOT NULL
                  AND s.created_at > {createdAfter} AND s.created_at <= {createdBefore}
                  AND NOT EXISTS (
                      SELECT 1 FROM jobs j
                      WHERE j.type = {type} AND j.status IN ({ready}, {running})
                        AND j.payload ->> 'providerId' = s.mp_preapproval_id)
                ORDER BY s.created_at
                LIMIT {limit}
                """)
            .ToListAsync(ct);
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

internal sealed class RefundRequestRepository(AppDbContext db) : IRefundRequestRepository
{
    public void Add(RefundRequest request) => db.RefundRequests.Add(request);

    public Task<RefundRequest?> FindAsync(Guid refundRequestId, CancellationToken ct) =>
        db.RefundRequests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == refundRequestId, ct);

    public Task<RefundRequest?> FindByPaymentAsync(Guid paymentId, CancellationToken ct) =>
        db.RefundRequests.AsNoTracking().FirstOrDefaultAsync(r => r.PaymentId == paymentId, ct);

    public async Task<IReadOnlySet<Guid>> PaymentsWithRequestAsync(IReadOnlyCollection<Guid> paymentIds, CancellationToken ct)
    {
        if (paymentIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        return (await db.RefundRequests
            .AsNoTracking()
            .Where(r => paymentIds.Contains(r.PaymentId))
            .Select(r => r.PaymentId)
            .ToListAsync(ct)).ToHashSet();
    }

    public async Task<bool> TryMarkPaidAsync(Guid refundRequestId, string mpRefundId, DateTimeOffset now, CancellationToken ct) =>
        await db.RefundRequests
            .Where(r => r.Id == refundRequestId && r.Status == RefundStatus.Requested)
            .ExecuteUpdateAsync(
                r => r
                    .SetProperty(x => x.Status, RefundStatus.Paid)
                    .SetProperty(x => x.MpRefundId, mpRefundId)
                    .SetProperty(x => x.DecidedAt, now),
                ct) == 1;
}
