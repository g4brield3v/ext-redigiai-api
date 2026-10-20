using System.Text.Json;
using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Billing;
using RedigiAI.Domain.Common;
using RedigiAI.Domain.Jobs;

namespace RedigiAI.Application.Billing;

/// <summary>Uma notificação do Mercado Pago como chegou: cabeçalhos, a consulta (<c>?data.id=…&amp;type=…</c>) e o corpo.</summary>
public sealed record MercadoPagoNotification(
    string? Signature, string? RequestId, string? QueryDataId, string? QueryType, byte[] Body);

public interface IPaymentWebhookService
{
    /// <summary>
    /// Confere a assinatura (<c>x-signature</c>), grava a notificação uma vez só e enfileira o processamento. Inválida:
    /// 401, e nada é gravado. Os créditos só mudam no job, depois de consultar o Mercado Pago.
    /// </summary>
    Task ReceiveMercadoPagoAsync(MercadoPagoNotification notification, CancellationToken ct);
}

internal sealed class PaymentWebhookService(
    IPaymentWebhookVerifier verifier,
    IWebhookEventRepository events,
    IJobQueue jobs,
    IUnitOfWork unitOfWork,
    TimeProvider time) : IPaymentWebhookService
{
    private const string PaymentTopic = "payment";

    /// <summary>As compras avulsas são orders: o aviso traz o id da order, consultado pelo mesmo job.</summary>
    private const string OrderTopic = "order";
    private const int MaxIdLength = 64;

    public async Task ReceiveMercadoPagoAsync(MercadoPagoNotification notification, CancellationToken ct)
    {
        var body = Read(notification.Body);
        var resourceId = notification.QueryDataId ?? body.DataId;
        var topic = notification.QueryType ?? body.Type;
        // O x-request-id entra na assinatura e identifica a entrega: sem ele, a notificação não é aceita.
        if (!IsId(resourceId) || !IsId(notification.RequestId)
            || !verifier.IsValid(notification.Signature, notification.RequestId, resourceId!))
        {
            throw Errors.Unauthenticated();
        }

        if (!IsId(topic))
        {
            throw Errors.Validation("Notificação sem tipo.");
        }

        // A chave contra duplicação vem do que é assinado (o corpo não é). Uma reentrega com outro x-request-id vira
        // outro evento, sem efeito: o job consulta o pagamento e só muda o que mudou lá.
        var externalId = $"request:{notification.RequestId}";
        // Guarda só os campos conhecidos, não o corpo inteiro.
        var summary = JsonSerializer.Serialize(new { id = body.Id, type = topic, action = body.Action, dataId = resourceId });
        var webhookEvent = WebhookEvent.Receive(WebhookEvent.MercadoPago, externalId, topic!, resourceId!, summary, time.GetUtcNow());
        await unitOfWork.InTransactionAsync(
            async () =>
            {
                if (!await events.TryAddAsync(webhookEvent, ct))
                {
                    return true;
                }

                switch (topic)
                {
                    case PaymentTopic or OrderTopic:
                        jobs.Enqueue(
                            JobTypes.ProcessPayment, new ProcessPaymentPayload(resourceId!, webhookEvent.Id), ProcessPaymentHandler.Attempts);
                        break;
                    case "subscription_preapproval" or "preapproval":
                        jobs.Enqueue(
                            JobTypes.ProcessSubscription,
                            new ProcessSubscriptionPayload(SubscriptionNotice.Subscription, resourceId!, webhookEvent.Id),
                            ProcessSubscriptionHandler.Attempts);
                        break;
                    case "subscription_authorized_payment" or "authorized_payment":
                        jobs.Enqueue(
                            JobTypes.ProcessSubscription,
                            new ProcessSubscriptionPayload(SubscriptionNotice.Charge, resourceId!, webhookEvent.Id),
                            ProcessSubscriptionHandler.Attempts);
                        break;
                }

                // Outros tópicos ficam só gravados.
                return true;
            },
            ct);
    }

    private static bool IsId(string? value) =>
        value is { Length: > 0 and <= MaxIdLength } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static (string? Id, string? Type, string? Action, string? DataId) Read(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return default;
            }

            var dataId = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object ? Text(data, "id") : null;
            var action = Text(root, "action");
            return (Text(root, "id"), Text(root, "type"), action is { Length: <= MaxIdLength } ? action : null, dataId);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    // O Mercado Pago manda ids ora como número, ora como texto.
    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;
}
