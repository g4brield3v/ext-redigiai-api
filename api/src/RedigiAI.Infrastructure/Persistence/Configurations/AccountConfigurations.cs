using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RedigiAI.Domain.Accounts;
using RedigiAI.Domain.Auditing;
using RedigiAI.Domain.Billing;
using RedigiAI.Domain.Essays;

namespace RedigiAI.Infrastructure.Persistence.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");
        builder.Property(u => u.ClerkUserId).HasMaxLength(64);
        builder.Property(u => u.Email).HasMaxLength(320);
        builder.Property(u => u.Name).HasMaxLength(User.MaxNameLength);
        builder.Property(u => u.TermsVersion).HasMaxLength(20);
        builder.Property(u => u.IsAdmin).HasDefaultValue(false);
        builder.ComplexProperty(u => u.EmailPreferences, preferences =>
        {
            preferences.Property(p => p.CorrectionReady).HasColumnName("notify_correction_ready");
            preferences.Property(p => p.IllegibleReminder).HasColumnName("notify_illegible_reminder");
            preferences.Property(p => p.NewThemes).HasColumnName("notify_new_themes");
        });
        builder.HasIndex(u => u.ClerkUserId).IsUnique();
        builder.HasIndex(u => u.LastActiveAt);
    }
}

internal sealed class CreditBucketConfiguration : IEntityTypeConfiguration<CreditBucket>
{
    public void Configure(EntityTypeBuilder<CreditBucket> builder)
    {
        builder.ToTable("credit_buckets", table =>
        {
            table.HasCheckConstraint("ck_credit_buckets_remaining", "remaining >= 0 AND remaining <= granted");
        });
        builder.Property(b => b.Source).HasMaxLength(20);
        builder.HasOne<User>().WithMany().HasForeignKey(b => b.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(b => b.UserId);

        // Teste grátis: no máximo um por conta, garantido pelo banco.
        builder.HasIndex(b => b.UserId, "ux_credit_buckets_trial_per_user")
            .HasDatabaseName("ux_credit_buckets_trial_per_user")
            .IsUnique()
            .HasFilter("source = 'trial'");

        // A semana de uma assinatura é concedida uma vez só, mesmo com dois workers ao mesmo tempo.
        builder.Property(b => b.GrantKey).HasMaxLength(120);
        builder.HasIndex(b => b.GrantKey).IsUnique().HasFilter("grant_key IS NOT NULL");

        // O vencimento do dia 1º procura os buckets vencidos que ainda têm saldo.
        builder.HasIndex(b => b.ExpiresAt).HasFilter("remaining > 0 AND expires_at IS NOT NULL");
    }
}

internal sealed class SubscriptionConfiguration : IEntityTypeConfiguration<Subscription>
{
    public void Configure(EntityTypeBuilder<Subscription> builder)
    {
        builder.ToTable("subscriptions", table =>
        {
            table.HasCheckConstraint("ck_subscriptions_card_last4", "card_last4 IS NULL OR card_last4 ~ '^[0-9]{4}$'");
        });
        builder.Property(s => s.MpPreapprovalId).HasMaxLength(100);
        builder.Property(s => s.IdempotencyKey).HasMaxLength(Subscription.MaxKeyLength);
        builder.Property(s => s.Status).HasMaxLength(20);
        builder.Property(s => s.CardLast4).HasMaxLength(4);
        builder.HasOne<User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(s => s.MpPreapprovalId).IsUnique().HasFilter("mp_preapproval_id IS NOT NULL");
        builder.HasIndex(s => new { s.UserId, s.IdempotencyKey }).IsUnique();
        builder.HasIndex(s => s.UserId);
        builder.HasIndex(s => s.Status);

        // No máximo uma assinatura aberta por aluno: dois checkouts concluídos não viram duas cobranças.
        builder.HasIndex(s => s.UserId, "ux_subscriptions_open_per_user")
            .HasDatabaseName("ux_subscriptions_open_per_user")
            .IsUnique()
            .HasFilter("status IN ('active', 'paused')");
        // Uma assinatura pendente por aluno: duas abas ao mesmo tempo, com chaves diferentes, não criam duas lá.
        builder.HasIndex(s => s.UserId, "ux_subscriptions_pending_per_user")
            .HasDatabaseName("ux_subscriptions_pending_per_user")
            .IsUnique()
            .HasFilter("status = 'pending'");
    }
}

internal sealed class CreditLedgerEntryConfiguration : IEntityTypeConfiguration<CreditLedgerEntry>
{
    public void Configure(EntityTypeBuilder<CreditLedgerEntry> builder)
    {
        builder.ToTable("credit_ledger");
        builder.Property(e => e.Source).HasMaxLength(20);
        builder.HasOne<User>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CreditBucket>().WithMany().HasForeignKey(e => e.BucketId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Essay>().WithMany().HasForeignKey(e => e.EssayId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Payment>().WithMany().HasForeignKey(e => e.PaymentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(e => e.UserId);
    }
}

internal sealed class AuditLogEntryConfiguration : IEntityTypeConfiguration<AuditLogEntry>
{
    public void Configure(EntityTypeBuilder<AuditLogEntry> builder)
    {
        builder.ToTable("audit_log");
        builder.Property(e => e.Actor).HasMaxLength(80);
        builder.Property(e => e.Action).HasMaxLength(80);
        builder.Property(e => e.Entity).HasMaxLength(80);
        builder.Property(e => e.Data).HasColumnType("jsonb");
        builder.HasIndex(e => new { e.Entity, e.EntityId });
    }
}

internal sealed class AccessLogEntryConfiguration : IEntityTypeConfiguration<AccessLogEntry>
{
    public void Configure(EntityTypeBuilder<AccessLogEntry> builder)
    {
        builder.ToTable("access_logs");
        builder.Property(e => e.Ip).HasMaxLength(64);
        builder.Property(e => e.UserAgent).HasMaxLength(AccessLogEntry.MaxUserAgentLength);
        builder.Property(e => e.SessionId).HasMaxLength(64);
        builder.HasOne<User>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(e => e.OccurredAt);
        builder.HasIndex(e => new { e.UserId, e.SessionId })
            .IsUnique()
            .HasFilter("session_id IS NOT NULL");
    }
}

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("payments", table =>
        {
            table.HasCheckConstraint("ck_payments_amount", "amount_cents > 0 AND quantity >= 1");
            // Pagamento aprovado sempre tem a data da aprovação.
            table.HasCheckConstraint(
                "ck_payments_approved_at",
                "status <> 'approved' OR approved_at IS NOT NULL");
        });
        builder.Property(p => p.MpPaymentId).HasMaxLength(64);
        builder.Property(p => p.Kind).HasMaxLength(20);
        builder.Property(p => p.Method).HasMaxLength(20);
        builder.Property(p => p.Status).HasMaxLength(20);
        builder.Property(p => p.IdempotencyKey).HasMaxLength(Payment.MaxKeyLength);
        builder.Property(p => p.PixQrCode).HasMaxLength(1000);
        builder.Property(p => p.PixQrCodeBase64).HasMaxLength(20000);
        builder.HasOne<User>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Subscription>().WithMany().HasForeignKey(p => p.SubscriptionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(p => p.MpPaymentId).IsUnique().HasFilter("mp_payment_id IS NOT NULL");
        builder.HasIndex(p => new { p.UserId, p.IdempotencyKey }).IsUnique();
        builder.HasIndex(p => new { p.UserId, p.CreatedAt });
    }
}

internal sealed class WebhookEventConfiguration : IEntityTypeConfiguration<WebhookEvent>
{
    public void Configure(EntityTypeBuilder<WebhookEvent> builder)
    {
        builder.ToTable("webhook_events");
        builder.Property(e => e.Provider).HasMaxLength(20);
        builder.Property(e => e.ExternalId).HasMaxLength(WebhookEvent.MaxExternalIdLength);
        builder.Property(e => e.Topic).HasMaxLength(64);
        builder.Property(e => e.ResourceId).HasMaxLength(64);
        builder.Property(e => e.Payload).HasColumnType("text");
        builder.HasIndex(e => new { e.Provider, e.ExternalId }).IsUnique();
    }
}

internal sealed class RefundRequestConfiguration : IEntityTypeConfiguration<RefundRequest>
{
    public void Configure(EntityTypeBuilder<RefundRequest> builder)
    {
        builder.ToTable("refund_requests", table =>
        {
            table.HasCheckConstraint("ck_refund_requests_amount", "amount_cents > 0 AND credits_removed >= 0");
        });
        builder.Property(r => r.Status).HasMaxLength(20);
        builder.Property(r => r.Reason).HasMaxLength(RefundRequest.MaxReasonLength);
        builder.Property(r => r.MpRefundId).HasMaxLength(64);
        builder.HasOne<User>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Restrict);
        // Um pedido por compra, garantido pelo banco.
        builder.HasOne<Payment>().WithMany().HasForeignKey(r => r.PaymentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(r => r.PaymentId).IsUnique();
        builder.HasIndex(r => r.UserId);
    }
}
