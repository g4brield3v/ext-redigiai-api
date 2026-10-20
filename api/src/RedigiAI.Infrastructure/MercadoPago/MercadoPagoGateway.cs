using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Billing;
using RedigiAI.Domain.Common;

namespace RedigiAI.Infrastructure.MercadoPago;

public sealed class MercadoPagoOptions
{
    public const string Section = "MercadoPago";

    /// <summary>Access token da conta (no desenvolvimento, só o de teste).</summary>
    public string AccessToken { get; set; } = "";

    /// <summary>A chave secreta das notificações (painel do Mercado Pago, Webhooks).</summary>
    public string WebhookSecret { get; set; } = "";

    public string BaseUrl { get; set; } = "https://api.mercadopago.com";
}

/// <summary>
/// A API de pagamentos do Mercado Pago (<c>/v1/payments</c>). O cartão chega só como o token gerado no navegador.
/// Nada do pagador (e-mail, documento) nem o token vai para o log.
/// </summary>
internal sealed class MercadoPagoGateway(IOptions<MercadoPagoOptions> options, ILogger<MercadoPagoGateway> logger)
    : IPaymentGateway
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeZoneInfo Brasilia = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public async Task<CreatedCharge> CreatePixAsync(ChargeRequest request, DateTimeOffset expiresAt, CancellationToken ct)
    {
        var body = Base(request);
        body["payment_method_id"] = "pix";
        // O Mercado Pago pede a validade com o fuso; vai no horário de Brasília.
        body["date_of_expiration"] = TimeZoneInfo.ConvertTime(expiresAt, Brasilia).ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);

        var created = await CreateAsync(body, request.PaymentId, ct);
        var transaction = created["point_of_interaction"]?["transaction_data"];
        var qrCode = transaction?["qr_code"]?.GetValue<string>();
        var qrCodeBase64 = transaction?["qr_code_base64"]?.GetValue<string>();
        if (qrCode is null || qrCodeBase64 is null)
        {
            logger.LogError("Mercado Pago created a Pix payment without a QR code");
            throw Errors.PaymentUnavailable();
        }

        return ReadCreated(created) with { PixQrCode = qrCode, PixQrCodeBase64 = qrCodeBase64, PixExpiresAt = expiresAt };
    }

    public async Task<CreatedCharge> CreateCardAsync(ChargeRequest request, CardToken card, CancellationToken ct)
    {
        var body = Base(request);
        body["token"] = card.Token;
        body["payment_method_id"] = card.PaymentMethodId;
        body["installments"] = 1;
        if (card.IssuerId is not null)
        {
            body["issuer_id"] = card.IssuerId;
        }

        if (card is { IdentificationType: { } type, IdentificationNumber: { } number })
        {
            body["payer"]!["identification"] = new JsonObject { ["type"] = type, ["number"] = number };
        }

        return ReadCreated(await CreateAsync(body, request.PaymentId, ct));
    }

    public async Task<ProviderPayment?> GetPaymentAsync(string providerId, CancellationToken ct)
    {
        using var message = Request(HttpMethod.Get, $"/v1/payments/{Uri.EscapeDataString(providerId)}");
        var (status, payment) = await SendAsync(message, ct);
        if (status == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (payment is null)
        {
            throw Errors.PaymentUnavailable();
        }

        return new ProviderPayment(
            Id(payment),
            StatusOf(payment),
            payment["external_reference"]?.GetValue<string>(),
            Cents(payment["transaction_amount"]),
            payment["currency_id"]?.GetValue<string>() ?? "",
            Math.Max(0, Cents(payment["transaction_amount_refunded"])),
            Text(payment["metadata"]?["preapproval_id"]));
    }

    // Assinaturas (preapproval). Os nomes dos campos seguem a API de assinaturas do Mercado Pago e precisam ser
    // conferidos no sandbox antes de ir para produção (docs/decisoes.md, etapa 4c).

    public async Task<CreatedSubscription> CreateSubscriptionAsync(SubscriptionRequest request, CancellationToken ct)
    {
        using var message = Request(HttpMethod.Post, "/preapproval");
        message.Headers.Add("X-Idempotency-Key", request.IdempotencyKey);
        // Checkout transparente: o cartão vem tokenizado do formulário do Mercado Pago na nossa página, e a assinatura
        // nasce autorizada, sem levar o aluno para fora da plataforma.
        message.Content = JsonContent.Create(new JsonObject
        {
            ["reason"] = request.Reason,
            ["external_reference"] = request.SubscriptionId.ToString(),
            ["payer_email"] = request.PayerEmail,
            ["card_token_id"] = request.Card.Token,
            ["back_url"] = request.BackUrl,
            ["status"] = "authorized",
            ["auto_recurring"] = new JsonObject
            {
                ["frequency"] = 1,
                ["frequency_type"] = "months",
                ["transaction_amount"] = request.AmountCents / 100m,
                ["currency_id"] = "BRL",
            },
        });
        var (status, created) = await SendAsync(message, ct);
        if (created is null)
        {
            throw status is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity
                ? Errors.PaymentFailed()
                : Errors.PaymentUnavailable();
        }

        return new CreatedSubscription(Id(created));
    }

    public async Task<ProviderSubscription?> GetSubscriptionAsync(string providerId, CancellationToken ct)
    {
        using var message = Request(HttpMethod.Get, $"/preapproval/{Uri.EscapeDataString(providerId)}");
        var (status, subscription) = await SendAsync(message, ct);
        if (status == HttpStatusCode.NotFound)
        {
            return null;
        }

        return subscription is null ? throw Errors.PaymentUnavailable() : SubscriptionFrom(subscription);
    }

    public async Task<ProviderSubscription?> FindSubscriptionByReferenceAsync(Guid subscriptionId, CancellationToken ct)
    {
        using var message = Request(HttpMethod.Get, $"/preapproval/search?external_reference={subscriptionId}");
        var (_, found) = await SendAsync(message, ct);
        if (found?["results"] is not JsonArray results)
        {
            throw Errors.PaymentUnavailable();
        }

        return results
            .OfType<JsonObject>()
            .Select(SubscriptionFrom)
            .FirstOrDefault(s => s.ExternalReference == subscriptionId.ToString() && s.Status != SubscriptionStatus.Cancelled);
    }

    private static ProviderSubscription SubscriptionFrom(JsonObject subscription) =>
        new(
            Id(subscription),
            subscription["status"]?.GetValue<string>() switch
            {
                "authorized" => SubscriptionStatus.Active,
                "paused" => SubscriptionStatus.Paused,
                "cancelled" => SubscriptionStatus.Cancelled,
                _ => SubscriptionStatus.Pending,
            },
            subscription["external_reference"]?.GetValue<string>(),
            Date(subscription["next_payment_date"]),
            Subscription.CardLast4From(subscription["last_four_digits"]?.GetValue<string>()));

    public async Task<ProviderSubscriptionCharge?> GetSubscriptionChargeAsync(string providerId, CancellationToken ct)
    {
        using var message = Request(HttpMethod.Get, $"/authorized_payments/{Uri.EscapeDataString(providerId)}");
        var (status, charge) = await SendAsync(message, ct);
        if (status == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (charge?["preapproval_id"]?.GetValue<string>() is not { } preapprovalId)
        {
            throw Errors.PaymentUnavailable();
        }

        var payment = charge["payment"] is JsonObject paid ? Text(paid["id"]) : null;
        return new ProviderSubscriptionCharge(Id(charge), preapprovalId, payment);
    }

    public async Task<string> RefundAsync(string paymentProviderId, long amountCents, Guid idempotencyKey, CancellationToken ct)
    {
        using var message = Request(HttpMethod.Post, $"/v1/payments/{Uri.EscapeDataString(paymentProviderId)}/refunds");
        // O mesmo pedido de arrependimento nunca vira dois reembolsos lá.
        message.Headers.Add("X-Idempotency-Key", idempotencyKey.ToString());
        message.Content = JsonContent.Create(new JsonObject { ["amount"] = amountCents / 100m });
        var (status, refund) = await SendAsync(message, ct);
        if (refund is null)
        {
            throw status is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity
                ? Errors.PaymentFailed()
                : Errors.PaymentUnavailable();
        }

        return Id(refund);
    }

    public async Task CancelSubscriptionAsync(string providerId, CancellationToken ct)
    {
        using var message = Request(HttpMethod.Put, $"/preapproval/{Uri.EscapeDataString(providerId)}");
        message.Content = JsonContent.Create(new JsonObject { ["status"] = "cancelled" });
        var (_, cancelled) = await SendAsync(message, ct);
        if (cancelled is null)
        {
            throw Errors.PaymentUnavailable();
        }
    }

    private static DateTimeOffset? Date(JsonNode? value) =>
        value is JsonValue node && node.TryGetValue<string>(out var text)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date.ToUniversalTime()
            : null;

    private static string? Text(JsonNode? value) =>
        value switch
        {
            JsonValue node when node.TryGetValue<long>(out var number) => number.ToString(CultureInfo.InvariantCulture),
            JsonValue node when node.TryGetValue<string>(out var text) => text,
            _ => null,
        };

    private static JsonObject Base(ChargeRequest request) =>
        new()
        {
            ["transaction_amount"] = request.AmountCents / 100m,
            ["description"] = request.Description,
            ["external_reference"] = request.PaymentId.ToString(),
            ["payer"] = new JsonObject { ["email"] = request.PayerEmail },
        };

    private async Task<JsonObject> CreateAsync(JsonObject body, Guid paymentId, CancellationToken ct)
    {
        using var message = Request(HttpMethod.Post, "/v1/payments");
        // A mesma compra nunca vira dois pagamentos lá: repetir a criação devolve o primeiro.
        message.Headers.Add("X-Idempotency-Key", paymentId.ToString());
        message.Content = JsonContent.Create(body);
        var (status, payment) = await SendAsync(message, ct);
        if (payment is not null)
        {
            return payment;
        }

        // 400 e 422: o Mercado Pago recusou o pedido (token de cartão inválido, por exemplo).
        throw status is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity
            ? Errors.PaymentFailed()
            : Errors.PaymentUnavailable();
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var settings = options.Value;
        if (settings.AccessToken.Length == 0)
        {
            logger.LogError("Mercado Pago is not configured: set MercadoPago__AccessToken");
            throw Errors.PaymentUnavailable();
        }

        var message = new HttpRequestMessage(method, $"{settings.BaseUrl.TrimEnd('/')}{path}");
        message.Headers.Authorization = new("Bearer", settings.AccessToken);
        return message;
    }

    /// <returns>O código e o corpo, quando a resposta foi 2xx; sem resposta, <c>PAYMENT_UNAVAILABLE</c>.</returns>
    private async Task<(HttpStatusCode Status, JsonObject? Body)> SendAsync(HttpRequestMessage message, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CallTimeout);
        try
        {
            using var response = await Http.SendAsync(message, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var (error, reason, causes) = await ErrorOf(response, timeout.Token);
                logger.LogWarning(
                    "Mercado Pago {Method} {Path} answered {Status}: {Error} {Reason} {Causes}",
                    message.Method,
                    message.RequestUri?.AbsolutePath,
                    (int)response.StatusCode,
                    error,
                    reason,
                    causes);
                return (response.StatusCode, null);
            }

            var body = await response.Content.ReadFromJsonAsync<JsonObject>(timeout.Token);
            return (response.StatusCode, body ?? throw Errors.PaymentUnavailable());
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning("Mercado Pago {Method} {Path} failed: {Error}", message.Method, message.RequestUri?.AbsolutePath, ex.GetType().Name);
            throw Errors.PaymentUnavailable();
        }
    }

    /// <summary>
    /// O motivo de uma recusa, para o log: <c>error</c>, <c>message</c> e os códigos de <c>cause</c>, curtos e sem
    /// caracteres de controle. Nunca o corpo inteiro nem as descrições das causas, que podem repetir dados enviados.
    /// </summary>
    private static async Task<(string? Error, string? Reason, string? Causes)> ErrorOf(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            if (JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) is not JsonObject body)
            {
                return (null, null, null);
            }

            var causes = body["cause"] is JsonArray list
                ? string.Join(',', list.OfType<JsonObject>().Select(c => Clean(Text(c["code"]))).Where(c => c is not null))
                : null;
            return (Clean(Text(body["error"])), Clean(Text(body["message"])), causes);
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }

    private static string? Clean(string? value) =>
        value is null ? null : new string(value.Where(c => !char.IsControl(c)).Take(200).ToArray());

    private static CreatedCharge ReadCreated(JsonObject payment) => new(Id(payment), StatusOf(payment));

    private static string Id(JsonObject resource) => Text(resource["id"]) ?? throw Errors.PaymentUnavailable();

    /// <summary>
    /// As situações do Mercado Pago na nossa: autorizado, em análise e em mediação ainda não são dinheiro recebido.
    /// </summary>
    private static PaymentStatus StatusOf(JsonObject payment) =>
        payment["status"]?.GetValue<string>() switch
        {
            "approved" => PaymentStatus.Approved,
            "rejected" => PaymentStatus.Rejected,
            "cancelled" => PaymentStatus.Cancelled,
            "refunded" => PaymentStatus.Refunded,
            "charged_back" => PaymentStatus.ChargedBack,
            _ => PaymentStatus.Pending,
        };

    private static long Cents(JsonNode? amount) =>
        amount is JsonValue value && value.TryGetValue<decimal>(out var reais)
            ? (long)Math.Round(reais * 100m, MidpointRounding.AwayFromZero)
            : -1;
}
