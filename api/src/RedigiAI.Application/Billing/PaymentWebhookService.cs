using System.Text;
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
    private const int MaxIdLength = 64;

    public async Task ReceiveMercadoPagoAsync(MercadoPagoNotification notification, CancellationToken ct)
    {
        var body = Read(notification.Body);
        var resourceId = notification.QueryDataId ?? body.DataId;
        var topic = notification.QueryType ?? body.Type;
        if (!IsId(resourceId) || !verifier.IsValid(notification.Signature, notification.RequestId, resourceId!))
        {
            throw Errors.Unauthenticated();
        }

        if (!IsId(topic))
        {
            throw Errors.Validation("Notificação sem tipo.");
        }

        // A reentrega da mesma notificação traz o mesmo id; sem ele, vale o x-request-id.
        var externalId = IsId(body.Id) ? $"notification:{body.Id}" : $"request:{notification.RequestId}";
        if (externalId.Length > WebhookEvent.MaxExternalIdLength)
        {
            throw Errors.Validation("Notificação inválida.");
        }

        var webhookEvent = WebhookEvent.Receive(
            WebhookEvent.MercadoPago, externalId, topic!, resourceId!, Encoding.UTF8.GetString(notification.Body), time.GetUtcNow());
        await unitOfWork.InTransactionAsync(
            async () =>
            {
                if (await events.TryAddAsync(webhookEvent, ct) && topic == PaymentTopic)
                {
                    jobs.Enqueue(
                        JobTypes.ProcessPayment, new ProcessPaymentPayload(resourceId!, webhookEvent.Id), ProcessPaymentHandler.Attempts);
                }

                // As notificações de assinatura ficam gravadas para a etapa do plano semanal.
                return true;
            },
            ct);
    }

    private static bool IsId(string? value) =>
        value is { Length: > 0 and <= MaxIdLength } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static (string? Id, string? Type, string? DataId) Read(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return default;
            }

            return (Text(root, "id"), Text(root, "type"), root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object ? Text(data, "id") : null);
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
