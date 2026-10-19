namespace RedigiAI.Domain.Billing;

/// <summary>
/// Notificação recebida de um provedor, gravada antes de qualquer processamento (especificação, seção 9). A
/// reentrega da mesma notificação não duplica: <see cref="ExternalId"/> é único por provedor.
/// </summary>
public sealed class WebhookEvent
{
    public const string MercadoPago = "mercadopago";
    public const int MaxExternalIdLength = 200;

    private WebhookEvent() { }

    public Guid Id { get; private set; }
    public string Provider { get; private set; } = null!;
    public string ExternalId { get; private set; } = null!;

    /// <summary>O tipo do recurso avisado ("payment", "subscription_preapproval"…).</summary>
    public string Topic { get; private set; } = null!;

    /// <summary>O id do recurso no provedor; o job busca o recurso por ele, nunca usa o corpo da notificação.</summary>
    public string ResourceId { get; private set; } = null!;

    /// <summary>O corpo recebido, guardado como prova.</summary>
    public string Payload { get; private set; } = null!;
    public DateTimeOffset ReceivedAt { get; private set; }
    public DateTimeOffset? ProcessedAt { get; private set; }

    public static WebhookEvent Receive(
        string provider, string externalId, string topic, string resourceId, string payload, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Provider = provider,
            ExternalId = externalId,
            Topic = topic,
            ResourceId = resourceId,
            Payload = payload,
            ReceivedAt = now,
        };
}
