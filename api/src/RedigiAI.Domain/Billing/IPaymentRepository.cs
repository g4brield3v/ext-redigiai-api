namespace RedigiAI.Domain.Billing;

public interface IPaymentRepository
{
    void Add(Payment payment);

    Task<Payment?> FindAsync(Guid paymentId, CancellationToken ct);

    /// <summary>A compra do aluno com esta <c>Idempotency-Key</c>.</summary>
    Task<Payment?> FindByKeyAsync(Guid userId, string idempotencyKey, CancellationToken ct);

    /// <summary>As compras do aluno que chegaram ao Mercado Pago, da mais recente para a mais antiga.</summary>
    Task<IReadOnlyList<Payment>> ListByUserAsync(Guid userId, int limit, CancellationToken ct);

    /// <summary>
    /// Grava o que o Mercado Pago devolveu na criação (o id e, no Pix, o QR code). Só vale enquanto o pagamento não
    /// tem id ou tem este mesmo: duas requisições com a mesma chave chegam ao mesmo pagamento lá.
    /// </summary>
    Task<bool> TryAttachProviderAsync(
        Guid paymentId, string mpPaymentId, string? pixQrCode, string? pixQrCodeBase64, DateTimeOffset? pixExpiresAt,
        DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Muda a situação com <c>UPDATE … WHERE status IN (…)</c> (regra 10), na transação aberta. Zero linhas: outro
    /// processo já avançou, ou a transição não é permitida.
    /// </summary>
    Task<bool> TryTransitionAsync(Guid paymentId, PaymentStatus to, DateTimeOffset now, CancellationToken ct);
}

public interface IWebhookEventRepository
{
    /// <summary>Grava o evento na transação aberta; se a mesma notificação já foi gravada, não grava nada.</summary>
    /// <returns><c>false</c> se era uma reentrega.</returns>
    Task<bool> TryAddAsync(WebhookEvent webhookEvent, CancellationToken ct);

    Task MarkProcessedAsync(Guid eventId, DateTimeOffset now, CancellationToken ct);
}
