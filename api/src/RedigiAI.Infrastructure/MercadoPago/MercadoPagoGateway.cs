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

    /// <summary>
    /// Só em desenvolvimento: o e-mail do comprador de teste, que assina o plano no lugar do aluno. No sandbox, a
    /// assinatura exige que pagador e recebedor sejam os dois contas de teste. Fora do desenvolvimento, a API não sobe
    /// com ele preenchido.
    /// </summary>
    public string TestPayerEmail { get; set; } = "";
}

/// <summary>
/// O Mercado Pago. As compras avulsas (Pix e cartão) são orders (<c>/v1/orders</c>, checkout transparente): o id da
/// compra lá é o da order (<c>ORD…</c>). As cobranças do plano mensal vêm da API de assinaturas e são pagamentos
/// (<c>/v1/payments</c>, id numérico); consulta e reembolso escolhem a API pelo id. O cartão chega só como o token
/// gerado no navegador. Nada do pagador (e-mail, documento) nem o token vai para o log.
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
        // A validade vai como duração (ISO 8601) a partir de agora, em minutos inteiros.
        var minutes = Math.Max(1, (int)Math.Ceiling((expiresAt - DateTimeOffset.UtcNow).TotalMinutes));
        var body = Order(request, new JsonObject { ["id"] = "pix", ["type"] = "bank_transfer" });
        body["expiration_time"] = $"PT{minutes}M";

        var created = await CreateOrderAsync(body, request.PaymentId, ct);
        var method = FirstPayment(created)?["payment_method"];
        var qrCode = method?["qr_code"]?.GetValue<string>();
        var qrCodeBase64 = method?["qr_code_base64"]?.GetValue<string>();
        if (qrCode is null || qrCodeBase64 is null)
        {
            logger.LogError("Mercado Pago created a Pix order without a QR code");
            throw Errors.PaymentUnavailable();
        }

        return new CreatedCharge(Id(created), OrderStatusOf(created), qrCode, qrCodeBase64, expiresAt);
    }

    public async Task<CreatedCharge> CreateCardAsync(ChargeRequest request, CardToken card, CancellationToken ct)
    {
        var method = new JsonObject
        {
            ["id"] = card.PaymentMethodId,
            ["type"] = CardTypeOf(card.PaymentMethodId),
            ["token"] = card.Token,
            ["installments"] = 1,
        };
        var body = Order(request, method);
        if (card is { IdentificationType: { } type, IdentificationNumber: { } number })
        {
            body["payer"]!["identification"] = new JsonObject { ["type"] = type, ["number"] = number };
        }

        var created = await CreateOrderAsync(body, request.PaymentId, ct);
        return new CreatedCharge(Id(created), OrderStatusOf(created));
    }

    public async Task<ProviderPayment?> GetPaymentAsync(string providerId, CancellationToken ct) =>
        IsOrder(providerId) ? await GetOrderAsync(providerId, ct) : await GetPlanPaymentAsync(providerId, ct);

    private async Task<ProviderPayment?> GetOrderAsync(string orderId, CancellationToken ct)
    {
        var order = await FindOrderAsync(orderId, ct);
        if (order is null)
        {
            return null;
        }

        var refunded = Math.Max(0, FirstPayment(order) is { } payment ? Cents(payment["refunded_amount"]) : 0);
        return new ProviderPayment(
            Id(order),
            OrderStatusOf(order),
            order["external_reference"]?.GetValue<string>(),
            Cents(order["total_amount"]),
            order["currency"]?.GetValue<string>() ?? "",
            refunded);
    }

    private async Task<JsonObject?> FindOrderAsync(string orderId, CancellationToken ct)
    {
        using var message = Request(HttpMethod.Get, $"/v1/orders/{Uri.EscapeDataString(orderId)}");
        var (status, order) = await SendAsync(message, ct);
        if (status == HttpStatusCode.NotFound)
        {
            return null;
        }

        return order ?? throw Errors.PaymentUnavailable();
    }

    /// <summary>Uma cobrança do plano mensal: um pagamento da API de assinaturas.</summary>
    private async Task<ProviderPayment?> GetPlanPaymentAsync(string paymentId, CancellationToken ct)
    {
        using var message = Request(HttpMethod.Get, $"/v1/payments/{Uri.EscapeDataString(paymentId)}");
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
            PaymentStatusOf(payment),
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
            ["payer_email"] = options.Value.TestPayerEmail is { Length: > 0 } test ? test : request.PayerEmail,
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
        if (!IsOrder(paymentProviderId))
        {
            return await RefundPlanPaymentAsync(paymentProviderId, amountCents, idempotencyKey, ct);
        }

        // O reembolso parcial da order vai na transação dela.
        var order = await FindOrderAsync(paymentProviderId, ct) ?? throw Errors.PaymentFailed();
        var transactionId = FirstPayment(order) is { } payment ? Text(payment["id"]) : null;
        if (transactionId is null)
        {
            throw Errors.PaymentUnavailable();
        }

        using var message = Request(HttpMethod.Post, $"/v1/orders/{Uri.EscapeDataString(paymentProviderId)}/refund");
        // O mesmo pedido de arrependimento nunca vira dois reembolsos lá.
        message.Headers.Add("X-Idempotency-Key", idempotencyKey.ToString());
        message.Content = JsonContent.Create(new JsonObject
        {
            ["transactions"] = new JsonArray(new JsonObject { ["id"] = transactionId, ["amount"] = Money(amountCents) }),
        });
        var (status, refunded) = await SendAsync(message, ct);
        if (refunded is null)
        {
            throw status is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity
                ? Errors.PaymentFailed()
                : Errors.PaymentUnavailable();
        }

        // O reembolso desta chamada é o último da transação.
        return refunded["transactions"]?["refunds"] is JsonArray refunds
            && refunds.OfType<JsonObject>().LastOrDefault(r => Text(r["transaction_id"]) == transactionId) is { } refund
            ? Id(refund)
            : throw Errors.PaymentUnavailable();
    }

    private async Task<string> RefundPlanPaymentAsync(string paymentId, long amountCents, Guid idempotencyKey, CancellationToken ct)
    {
        using var message = Request(HttpMethod.Post, $"/v1/payments/{Uri.EscapeDataString(paymentId)}/refunds");
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

    /// <summary>Uma order online, processada na criação, com um pagamento só.</summary>
    private static JsonObject Order(ChargeRequest request, JsonObject paymentMethod) =>
        new()
        {
            ["type"] = "online",
            ["processing_mode"] = "automatic",
            ["external_reference"] = request.PaymentId.ToString(),
            ["description"] = request.Description,
            ["total_amount"] = Money(request.AmountCents),
            ["payer"] = new JsonObject { ["email"] = request.PayerEmail },
            ["transactions"] = new JsonObject
            {
                ["payments"] = new JsonArray(new JsonObject { ["amount"] = Money(request.AmountCents), ["payment_method"] = paymentMethod }),
            },
        };

    private async Task<JsonObject> CreateOrderAsync(JsonObject body, Guid paymentId, CancellationToken ct)
    {
        using var message = Request(HttpMethod.Post, "/v1/orders");
        // A mesma compra nunca vira duas orders lá: repetir a criação devolve a primeira.
        message.Headers.Add("X-Idempotency-Key", paymentId.ToString());
        message.Content = JsonContent.Create(body);
        var (status, order) = await SendAsync(message, ct, keepRejectedOrder: true);
        if (order is not null)
        {
            return order;
        }

        // 400 e 422: o Mercado Pago recusou o pedido (token de cartão inválido, por exemplo).
        throw status is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity
            ? Errors.PaymentFailed()
            : Errors.PaymentUnavailable();
    }

    private static bool IsOrder(string providerId) => providerId.StartsWith("ORD", StringComparison.Ordinal);

    private static JsonObject? FirstPayment(JsonObject order) =>
        order["transactions"]?["payments"] is JsonArray payments ? payments.OfType<JsonObject>().FirstOrDefault() : null;

    /// <summary>Os cartões de débito do Mercado Pago têm id começando por "deb" (debelo, debvisa, debmaster…).</summary>
    private static string CardTypeOf(string paymentMethodId) =>
        paymentMethodId.StartsWith("deb", StringComparison.OrdinalIgnoreCase) ? "debit_card" : "credit_card";

    /// <summary>Valores das orders: texto com duas casas, ponto decimal.</summary>
    private static string Money(long cents) => (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture);

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
    /// <param name="keepRejectedOrder">
    /// Na criação de order: a recusa do cartão vem como 402 com a order em <c>data</c>, que é devolvida como resposta.
    /// </param>
    private async Task<(HttpStatusCode Status, JsonObject? Body)> SendAsync(
        HttpRequestMessage message, CancellationToken ct, bool keepRejectedOrder = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CallTimeout);
        try
        {
            using var response = await Http.SendAsync(message, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(timeout.Token);
                if (keepRejectedOrder && response.StatusCode == HttpStatusCode.PaymentRequired
                    && ParseObject(text)?["data"] is JsonObject rejected)
                {
                    return (response.StatusCode, rejected.DeepClone().AsObject());
                }

                var (error, reason, causes) = ErrorOf(text);
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
    private static (string? Error, string? Reason, string? Causes) ErrorOf(string text)
    {
        if (ParseObject(text) is not { } body)
        {
            return (null, null, null);
        }

        var causes = body["cause"] is JsonArray list
            ? string.Join(',', list.OfType<JsonObject>().Select(c => Clean(Text(c["code"]))).Where(c => c is not null))
            : body["errors"] is JsonArray errors
                ? string.Join(',', errors.OfType<JsonObject>().Select(e => Clean(Text(e["code"]))).Where(c => c is not null))
                : null;
        return (Clean(Text(body["error"])), Clean(Text(body["message"])), causes);
    }

    private static JsonObject? ParseObject(string text)
    {
        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Clean(string? value) =>
        value is null ? null : new string(value.Where(c => !char.IsControl(c)).Take(200).ToArray());

    private static string Id(JsonObject resource) => Text(resource["id"]) ?? throw Errors.PaymentUnavailable();

    /// <summary>
    /// A situação de uma order na nossa. Reembolsada em parte continua aprovada (o valor estornado vem à parte);
    /// criada, aguardando pagamento e em análise ainda não são dinheiro recebido.
    /// </summary>
    internal static PaymentStatus OrderStatusOf(JsonObject order)
    {
        var detail = order["status_detail"]?.GetValue<string>();
        var transaction = FirstPayment(order);
        if (detail == "charged_back" || transaction?["status"]?.GetValue<string>() == "charged_back"
            || transaction?["status_detail"]?.GetValue<string>() == "charged_back")
        {
            return PaymentStatus.ChargedBack;
        }

        return (order["status"]?.GetValue<string>(), detail) switch
        {
            ("processed", "refunded") or ("refunded", _) => PaymentStatus.Refunded,
            ("processed", _) => PaymentStatus.Approved,
            ("failed", _) => PaymentStatus.Rejected,
            ("canceled" or "cancelled" or "expired", _) => PaymentStatus.Cancelled,
            _ => PaymentStatus.Pending,
        };
    }

    /// <summary>
    /// As situações de um pagamento (cobrança do plano) na nossa: autorizado, em análise e em mediação ainda não são
    /// dinheiro recebido.
    /// </summary>
    private static PaymentStatus PaymentStatusOf(JsonObject payment) =>
        payment["status"]?.GetValue<string>() switch
        {
            "approved" => PaymentStatus.Approved,
            "rejected" => PaymentStatus.Rejected,
            "cancelled" => PaymentStatus.Cancelled,
            "refunded" => PaymentStatus.Refunded,
            "charged_back" => PaymentStatus.ChargedBack,
            _ => PaymentStatus.Pending,
        };

    /// <summary>Valor em centavos; as orders mandam texto ("32.00"), os pagamentos, número.</summary>
    private static long Cents(JsonNode? amount) =>
        amount is JsonValue value
        && (value.TryGetValue<decimal>(out var reais)
            || (value.TryGetValue<string>(out var text)
                && decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out reais)))
            ? (long)Math.Round(reais * 100m, MidpointRounding.AwayFromZero)
            : -1;
}
