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
            payment["currency_id"]?.GetValue<string>() ?? "");
    }

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
                logger.LogWarning(
                    "Mercado Pago {Method} {Path} answered {Status}", message.Method, message.RequestUri?.AbsolutePath, (int)response.StatusCode);
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

    private static CreatedCharge ReadCreated(JsonObject payment) => new(Id(payment), StatusOf(payment));

    private static string Id(JsonObject payment) =>
        payment["id"] switch
        {
            JsonValue value when value.TryGetValue<long>(out var number) => number.ToString(CultureInfo.InvariantCulture),
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            _ => throw Errors.PaymentUnavailable(),
        };

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
