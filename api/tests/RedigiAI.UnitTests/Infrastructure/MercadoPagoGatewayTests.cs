using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Billing;
using RedigiAI.Domain.Common;
using RedigiAI.Infrastructure.MercadoPago;

namespace RedigiAI.UnitTests.Infrastructure;

/// <summary>
/// O gateway contra um Mercado Pago local que responde como o sandbox respondeu (API de Orders, outubro de 2026).
/// </summary>
public sealed class MercadoPagoGatewayTests : IDisposable
{
    private const string OrderId = "ORDTST01M5BNY4QVJJGCAPTASDX363BM";
    private const string TransactionId = "PAY01M5BNY4R9CVY7PCZHPRW3MVFJ";

    private readonly FakeMercadoPago _server = new();
    private readonly MercadoPagoGateway _gateway;

    public MercadoPagoGatewayTests()
    {
        _gateway = new MercadoPagoGateway(
            Options.Create(new MercadoPagoOptions { AccessToken = "TEST-token", BaseUrl = _server.Url }),
            NullLogger<MercadoPagoGateway>.Instance);
    }

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task A_card_purchase_is_created_as_an_order_with_one_installment_and_the_purchase_id_as_key()
    {
        _server.Reply("POST /v1/orders", 201, Order("processed", "accredited"));
        var request = Charge();

        var created = await _gateway.CreateCardAsync(request, new CardToken("tok", "visa", "25", "CPF", "12345678909"), CancellationToken.None);

        Assert.Equal((OrderId, PaymentStatus.Approved), (created.ProviderId, created.Status));
        var sent = Assert.Single(_server.Requests);
        Assert.Equal(request.PaymentId.ToString(), sent.Headers["X-Idempotency-Key"]);
        Assert.Equal("Bearer TEST-token", sent.Headers["Authorization"]);
        var body = JsonNode.Parse(sent.Body)!;
        Assert.Equal("online", body["type"]!.GetValue<string>());
        Assert.Equal("16.00", body["total_amount"]!.GetValue<string>());
        Assert.Equal(request.PaymentId.ToString(), body["external_reference"]!.GetValue<string>());
        var method = body["transactions"]!["payments"]![0]!["payment_method"]!;
        Assert.Equal(("visa", "credit_card", "tok", 1), (
            method["id"]!.GetValue<string>(), method["type"]!.GetValue<string>(), method["token"]!.GetValue<string>(),
            method["installments"]!.GetValue<int>()));
        Assert.Equal("12345678909", body["payer"]!["identification"]!["number"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_debit_card_goes_as_debit()
    {
        _server.Reply("POST /v1/orders", 201, Order("processed", "accredited"));

        await _gateway.CreateCardAsync(Charge(), new CardToken("tok", "debelo", null, null, null), CancellationToken.None);

        var body = JsonNode.Parse(Assert.Single(_server.Requests).Body)!;
        Assert.Equal("debit_card", body["transactions"]!["payments"]![0]!["payment_method"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_declined_card_comes_as_402_with_the_order_and_becomes_a_rejected_purchase()
    {
        var order = Order("failed", "failed", transactionStatus: "failed", transactionDetail: "rejected_by_issuer");
        _server.Reply("POST /v1/orders", 402, $$"""{"errors":[{"code":"failed","message":"The following transactions failed"}],"data":{{order}}}""");

        var created = await _gateway.CreateCardAsync(Charge(), new CardToken("tok", "visa", null, null, null), CancellationToken.None);

        Assert.Equal((OrderId, PaymentStatus.Rejected), (created.ProviderId, created.Status));
    }

    [Fact]
    public async Task A_bad_card_token_is_a_payment_failure_and_mercado_pago_down_is_unavailable()
    {
        _server.Reply("POST /v1/orders", 400, """{"errors":[{"code":"invalid_card_token"}]}""");
        var bad = await Assert.ThrowsAsync<AppError>(() => _gateway.CreateCardAsync(Charge(), new CardToken("x", "visa", null, null, null), CancellationToken.None));
        _server.Reply("POST /v1/orders", 401, """{"message":"unauthorized"}""");
        var down = await Assert.ThrowsAsync<AppError>(() => _gateway.CreateCardAsync(Charge(), new CardToken("x", "visa", null, null, null), CancellationToken.None));

        Assert.Equal(("PAYMENT_FAILED", "PAYMENT_UNAVAILABLE"), (bad.Code, down.Code));
    }

    [Fact]
    public async Task A_pix_order_returns_the_qr_code_and_asks_for_the_validity()
    {
        var method = """{"id":"pix","type":"bank_transfer","qr_code":"00020126580014br.gov.bcb.pix","qr_code_base64":"iVBORw0KGgo="}""";
        _server.Reply("POST /v1/orders", 201, Order("action_required", "waiting_transfer", transactionStatus: "action_required", method: method));
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(30);

        var created = await _gateway.CreatePixAsync(Charge(), expiresAt, CancellationToken.None);

        Assert.Equal(PaymentStatus.Pending, created.Status);
        Assert.Equal(("00020126580014br.gov.bcb.pix", "iVBORw0KGgo="), (created.PixQrCode, created.PixQrCodeBase64));
        var body = JsonNode.Parse(Assert.Single(_server.Requests).Body)!;
        Assert.Equal("PT30M", body["expiration_time"]!.GetValue<string>());
        Assert.Equal("bank_transfer", body["transactions"]!["payments"]![0]!["payment_method"]!["type"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("processed", "accredited", PaymentStatus.Approved)]
    [InlineData("processed", "partially_refunded", PaymentStatus.Approved)]
    [InlineData("processed", "refunded", PaymentStatus.Refunded)]
    [InlineData("refunded", "refunded", PaymentStatus.Refunded)]
    [InlineData("failed", "failed", PaymentStatus.Rejected)]
    [InlineData("canceled", "canceled", PaymentStatus.Cancelled)]
    [InlineData("action_required", "waiting_transfer", PaymentStatus.Pending)]
    [InlineData("processing", "in_process", PaymentStatus.Pending)]
    [InlineData("created", "created", PaymentStatus.Pending)]
    [InlineData("processed", "charged_back", PaymentStatus.ChargedBack)]
    [InlineData("charged_back", "charged_back", PaymentStatus.ChargedBack)]
    [InlineData("processed", "in_mediation", PaymentStatus.Pending)]
    [InlineData("processed", "um_detalhe_novo", PaymentStatus.Pending)]
    public async Task The_order_status_is_read_as_ours(string status, string detail, PaymentStatus expected)
    {
        _server.Reply($"GET /v1/orders/{OrderId}", 200, Order(status, detail));

        var payment = await _gateway.GetPaymentAsync(OrderId, CancellationToken.None);

        Assert.Equal(expected, payment!.Status);
    }

    [Fact]
    public async Task A_partially_refunded_order_reports_the_amount_refunded()
    {
        _server.Reply($"GET /v1/orders/{OrderId}", 200, Order("processed", "partially_refunded", refunded: "16.00"));

        var payment = await _gateway.GetPaymentAsync(OrderId, CancellationToken.None);

        Assert.Equal((3200L, 1600L, "BRL", "compra-1"), (payment!.AmountCents, payment.RefundedCents, payment.Currency, payment.ExternalReference));
    }

    [Fact]
    public async Task Without_refunded_amount_the_refunds_of_the_transaction_are_summed()
    {
        var order = JsonNode.Parse(Order("processed", "partially_refunded"))!.AsObject();
        order["transactions"]!["refunds"] = JsonNode.Parse(
            $$$"""[{"id":"R1","transaction_id":"{{{TransactionId}}}","amount":"16.00","status":"processed"},{"id":"R2","transaction_id":"{{{TransactionId}}}","amount":"5.00","status":"failed"}]""");
        _server.Reply($"GET /v1/orders/{OrderId}", 200, order.ToJsonString());

        var payment = await _gateway.GetPaymentAsync(OrderId, CancellationToken.None);

        Assert.Equal(1600L, payment!.RefundedCents);
    }

    [Fact]
    public async Task A_lowercase_order_id_is_still_an_order()
    {
        _server.Reply($"GET /v1/orders/{OrderId.ToLowerInvariant()}", 200, Order("processed", "accredited"));

        var payment = await _gateway.GetPaymentAsync(OrderId.ToLowerInvariant(), CancellationToken.None);

        Assert.Equal(PaymentStatus.Approved, payment!.Status);
    }

    [Fact]
    public async Task A_plan_charge_is_still_read_from_the_payments_api()
    {
        _server.Reply("GET /v1/payments/123456", 200,
            """{"id":123456,"status":"approved","external_reference":"sub-1","transaction_amount":39.9,"currency_id":"BRL","metadata":{"preapproval_id":"pre-1"}}""");

        var payment = await _gateway.GetPaymentAsync("123456", CancellationToken.None);

        Assert.Equal((PaymentStatus.Approved, 3990L, "pre-1"), (payment!.Status, payment.AmountCents, payment.PreapprovalId));
    }

    [Fact]
    public async Task A_refund_goes_to_the_order_transaction_with_the_request_as_key()
    {
        _server.Reply($"GET /v1/orders/{OrderId}", 200, Order("processed", "accredited"));
        _server.Reply($"POST /v1/orders/{OrderId}/refund", 201,
            $$$"""{"id":"{{{OrderId}}}","status":"processed","status_detail":"partially_refunded","transactions":{"refunds":[{"id":"REF01","transaction_id":"{{{TransactionId}}}","amount":"16.00","status":"processed"}]}}""");
        var key = Guid.NewGuid();

        var refundId = await _gateway.RefundAsync(OrderId, 1600, key, CancellationToken.None);

        Assert.Equal("REF01", refundId);
        var sent = _server.Requests.Single(r => r.Method == "POST");
        Assert.Equal(key.ToString(), sent.Headers["X-Idempotency-Key"]);
        var refund = JsonNode.Parse(sent.Body)!["transactions"]![0]!;
        Assert.Equal((TransactionId, "16.00"), (refund["id"]!.GetValue<string>(), refund["amount"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_subscription_charged_later_sends_the_start_date_in_brasilia_time()
    {
        _server.Reply("POST /preapproval", 201, """{"id":"pre-1","status":"authorized"}""");
        var firstChargeAt = new DateTimeOffset(2026, 11, 20, 14, 30, 0, TimeSpan.Zero);

        await _gateway.CreateSubscriptionAsync(
            new SubscriptionRequest(Guid.NewGuid(), "key", 3990, "plano", "aluna@example.com", new CardToken("tok", "master", null, null, null), "https://redigiai.com.br/perfil", firstChargeAt),
            CancellationToken.None);
        await _gateway.CreateSubscriptionAsync(
            new SubscriptionRequest(Guid.NewGuid(), "key-2", 3990, "plano", "aluna@example.com", new CardToken("tok", "master", null, null, null), "https://redigiai.com.br/perfil"),
            CancellationToken.None);

        var bodies = _server.Requests.Select(r => JsonNode.Parse(r.Body)!["auto_recurring"]!).ToList();
        Assert.Equal("2026-11-20T11:30:00.000-03:00", bodies[0]["start_date"]!.GetValue<string>());
        Assert.Null(bodies[1]["start_date"]);
    }

    private static ChargeRequest Charge() => new(Guid.NewGuid(), 1600, "RedigiAI: 1 correção avulsa", "aluna@example.com");

    private static string Order(
        string status,
        string detail,
        string? transactionStatus = null,
        string? transactionDetail = null,
        string? refunded = null,
        string? method = null) =>
        $$$"""
        {"id":"{{{OrderId}}}","type":"online","external_reference":"compra-1","total_amount":"32.00","currency":"BRL",
         "status":"{{{status}}}","status_detail":"{{{detail}}}",
         "transactions":{"payments":[{"id":"{{{TransactionId}}}","amount":"32.00",
           "status":"{{{transactionStatus ?? status}}}","status_detail":"{{{transactionDetail ?? detail}}}"
           {{{(refunded is null ? "" : $",\"refunded_amount\":\"{refunded}\"")}}},
           "payment_method":{{{method ?? CardMethod}}}}]}}
        """;

    private const string CardMethod = """{"id":"visa","type":"credit_card","installments":1}""";

    /// <summary>Um servidor HTTP local: responde o que o teste mandou e guarda os pedidos.</summary>
    private sealed class FakeMercadoPago : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly ConcurrentDictionary<string, (int Status, string Body)> _replies = new();

        public FakeMercadoPago()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Url = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _ = ServeAsync();
        }

        public string Url { get; }

        public ConcurrentQueue<(string Method, string Path, string Body, Dictionary<string, string> Headers)> Requests { get; } = new();

        public void Reply(string route, int status, string body) => _replies[route] = (status, body);

        public void Dispose() => _listener.Close();

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                using var reader = new StreamReader(context.Request.InputStream);
                var body = await reader.ReadToEndAsync();
                var headers = context.Request.Headers.AllKeys.Where(k => k is not null).ToDictionary(k => k!, k => context.Request.Headers[k]!);
                var route = $"{context.Request.HttpMethod} {context.Request.Url!.AbsolutePath}";
                Requests.Enqueue((context.Request.HttpMethod, context.Request.Url.AbsolutePath, body, headers));
                var (status, reply) = _replies.TryGetValue(route, out var found) ? found : (404, "{}");
                context.Response.StatusCode = status;
                context.Response.ContentType = "application/json";
                var bytes = Encoding.UTF8.GetBytes(reply);
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        }
    }
}
