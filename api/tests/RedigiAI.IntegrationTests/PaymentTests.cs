using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Billing;
using RedigiAI.Domain.Common;
using RedigiAI.Domain.Jobs;

namespace RedigiAI.IntegrationTests;

/// <summary>
/// Compra de correções avulsas (etapa 4b): checkout por Pix e cartão, webhook do Mercado Pago e o job que consulta o
/// pagamento lá antes de conceder qualquer crédito. O Mercado Pago é o simulado da fixture.
/// </summary>
[Collection(ApiCollection.Name)]
public class PaymentTests(ApiFixture api)
{
    [Fact]
    public async Task Pix_checkout_shows_the_qr_code_and_credits_only_after_the_payment_is_confirmed_there()
    {
        var student = await Buyer.CreateAsync(api);

        var response = await student.CheckoutAsync(new { quantity = 2, method = "pix" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payment = await response.Content.ReadFromJsonAsync<JsonElement>();
        var paymentId = payment.GetProperty("id").GetGuid();
        Assert.Equal(3200, payment.GetProperty("amountCents").GetInt64());
        Assert.Equal("pending", payment.GetProperty("status").GetString());
        var pix = payment.GetProperty("pix");
        Assert.StartsWith("00020126", pix.GetProperty("copyPaste").GetString());
        Assert.False(string.IsNullOrEmpty(pix.GetProperty("qrCodeBase64").GetString()));
        Assert.InRange(pix.GetProperty("expiresAt").GetDateTimeOffset() - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(31));
        Assert.Equal(3200, Assert.Single(api.Payments.Requests, r => r.Request.PaymentId == paymentId).Request.AmountCents);

        // Ainda não pago: o job consulta e não concede nada.
        await RunPaymentJobsAsync();
        Assert.Equal(1, await student.CreditsAsync());

        api.Payments.Set(api.Payments.ProviderIdOf(paymentId)!, PaymentStatus.Approved);
        await student.NotifyAsync(paymentId);
        await RunPaymentJobsAsync();
        await RunPaymentJobsAsync();

        Assert.Equal(3, await student.CreditsAsync());
        var approved = await student.Client.GetFromJsonAsync<JsonElement>($"/v1/payments/{paymentId}");
        Assert.Equal("approved", approved.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, approved.GetProperty("pix").ValueKind);
        Assert.True(approved.GetProperty("refundable").GetBoolean());
        var grant = Assert.Single(await api.QueryAsync(db => db.CreditLedger.Where(e => e.PaymentId == paymentId).ToListAsync()));
        Assert.Equal(2, grant.Delta);
        Assert.Equal(CreditSource.Single, grant.Source);
        Assert.Equal(1, await api.QueryAsync(db => db.AuditLog.CountAsync(a => a.Action == "payment.approved" && a.EntityId == paymentId)));
    }

    [Fact]
    public async Task The_same_idempotency_key_returns_the_same_purchase_and_charges_once()
    {
        var student = await Buyer.CreateAsync(api);
        var key = Guid.NewGuid().ToString();

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => student.CheckoutAsync(new { quantity = 1, method = "pix" }, key)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var ids = await Task.WhenAll(responses.Select(async r => (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid()));
        Assert.Single(ids.Distinct());
        Assert.Equal(1, await api.QueryAsync(db => db.Payments.CountAsync(p => p.UserId == student.Id)));
        Assert.NotNull(api.Payments.ProviderIdOf(ids[0]));
    }

    [Fact]
    public async Task The_request_needs_an_idempotency_key_a_quantity_from_1_to_10_and_a_method()
    {
        var student = await Buyer.CreateAsync(api);

        var noKey = await student.Client.PostAsJsonAsync("/v1/checkout/single", new { quantity = 1, method = "pix" });
        var zero = await student.CheckoutAsync(new { quantity = 0, method = "pix" });
        var eleven = await student.CheckoutAsync(new { quantity = 11, method = "pix" });
        var noMethod = await student.CheckoutAsync(new { quantity = 1 });
        var cardWithoutToken = await student.CheckoutAsync(new { quantity = 1, method = "card", card = new { paymentMethodId = "visa" } });

        Assert.All(
            new[] { noKey, zero, eleven, noMethod, cardWithoutToken },
            r => Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode));
        Assert.Equal(0, await api.QueryAsync(db => db.Payments.CountAsync(p => p.UserId == student.Id)));
    }

    [Fact]
    public async Task Buyers_aged_16_or_17_must_declare_the_guardian_authorization()
    {
        var minor = await Buyer.CreateAsync(api, birthDate: DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-17).AddDays(-3));

        var refused = await minor.CheckoutAsync(new { quantity = 1, method = "pix" });
        var accepted = await minor.CheckoutAsync(new { quantity = 1, method = "pix", guardianAuthorized = true });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("GUARDIAN_AUTHORIZATION_REQUIRED", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        var id = (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.NotNull(await api.QueryAsync(db => db.Payments.Where(p => p.Id == id).Select(p => p.GuardianAuthorizedAt).SingleAsync()));

        // Adulto não precisa da declaração, e ela não é gravada.
        var adult = await Buyer.CreateAsync(api);
        var adultPurchase = await adult.CheckoutAsync(new { quantity = 1, method = "pix", guardianAuthorized = true });
        var adultId = (await adultPurchase.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Null(await api.QueryAsync(db => db.Payments.Where(p => p.Id == adultId).Select(p => p.GuardianAuthorizedAt).SingleAsync()));
    }

    [Fact]
    public async Task A_card_refused_on_creation_answers_payment_failed_and_never_credits()
    {
        var student = await Buyer.CreateAsync(api);
        api.Payments.NextCardStatus = PaymentStatus.Rejected;
        var key = Guid.NewGuid().ToString();
        try
        {
            var first = await student.CheckoutAsync(Card(), key);
            var again = await student.CheckoutAsync(Card(), key);

            Assert.Equal(HttpStatusCode.PaymentRequired, first.StatusCode);
            Assert.Equal("PAYMENT_FAILED", (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
            Assert.Equal(HttpStatusCode.PaymentRequired, again.StatusCode);
            var payment = await api.QueryAsync(db => db.Payments.SingleAsync(p => p.UserId == student.Id));
            Assert.Equal(PaymentStatus.Rejected, payment.Status);
            Assert.Equal(1, await student.CreditsAsync());
        }
        finally
        {
            api.Payments.NextCardStatus = PaymentStatus.Pending;
        }
    }

    [Fact]
    public async Task An_approved_card_is_confirmed_by_the_job_and_the_token_is_the_only_card_data_sent()
    {
        var student = await Buyer.CreateAsync(api);
        api.Payments.NextCardStatus = PaymentStatus.Approved;
        try
        {
            var response = await student.CheckoutAsync(Card());
            var payment = await response.Content.ReadFromJsonAsync<JsonElement>();
            var paymentId = payment.GetProperty("id").GetGuid();

            // A resposta da criação não concede nada: a aprovação vem da consulta feita pelo job.
            Assert.Equal("pending", payment.GetProperty("status").GetString());
            Assert.Equal(1, await student.CreditsAsync());
            await RunPaymentJobsAsync();

            Assert.Equal(2, await student.CreditsAsync());
            var card = Assert.Single(api.Payments.Requests, r => r.Request.PaymentId == paymentId).Card!;
            Assert.Equal("tok_test_123", card.Token);
            Assert.Equal("visa", card.PaymentMethodId);
        }
        finally
        {
            api.Payments.NextCardStatus = PaymentStatus.Pending;
        }
    }

    [Fact]
    public async Task When_mercado_pago_is_down_nothing_is_charged_and_the_same_key_tries_again()
    {
        var student = await Buyer.CreateAsync(api);
        var key = Guid.NewGuid().ToString();
        api.Payments.Failures.Enqueue(Errors.PaymentUnavailable());

        var down = await student.CheckoutAsync(new { quantity = 1, method = "pix" }, key);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
        Assert.Empty((await student.Client.GetFromJsonAsync<JsonElement>("/v1/payments")).GetProperty("items").EnumerateArray());

        var retry = await student.CheckoutAsync(new { quantity = 1, method = "pix" }, key);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(1, await api.QueryAsync(db => db.Payments.CountAsync(p => p.UserId == student.Id)));
    }

    [Fact]
    public async Task The_webhook_needs_a_valid_signature_and_a_redelivery_is_recorded_once()
    {
        var student = await Buyer.CreateAsync(api);
        var paymentId = await student.BuyPixAsync();
        var providerId = api.Payments.ProviderIdOf(paymentId)!;
        var notificationId = Random.Shared.NextInt64(1, long.MaxValue).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var json = $$$"""{"id":{{{notificationId}}},"type":"payment","action":"payment.updated","data":{"id":"{{{providerId}}}"}}""";
        var client = api.CreateClient();

        var forged = await client.SendAsync(ApiFixture.CreateMercadoPagoWebhook(providerId, json, secret: "outra-chave"));
        var unsigned = await client.PostAsync($"/v1/webhooks/mercadopago?data.id={providerId}&type=payment", new StringContent(json));
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        Assert.Equal(0, await api.QueryAsync(db => db.WebhookEvents.CountAsync(e => e.ResourceId == providerId)));

        // Uma assinatura antiga (capturada e reenviada) e uma notificação sem x-request-id também não valem.
        var replayed = await client.SendAsync(ApiFixture.CreateMercadoPagoWebhook(providerId, json, signedAt: DateTimeOffset.UtcNow.AddMinutes(-30)));
        var withoutRequestId = await client.SendAsync(ApiFixture.CreateMercadoPagoWebhook(providerId, json, requestId: null));
        Assert.Equal(HttpStatusCode.Unauthorized, replayed.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, withoutRequestId.StatusCode);

        var requestId = $"req-{Guid.NewGuid():N}";
        var first = await client.SendAsync(ApiFixture.CreateMercadoPagoWebhook(providerId, json, requestId: requestId));
        var again = await client.SendAsync(ApiFixture.CreateMercadoPagoWebhook(providerId, json, requestId: requestId));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(1, await api.QueryAsync(db => db.WebhookEvents.CountAsync(e => e.ResourceId == providerId)));
        var fromWebhook = (await PaymentJobsAsync(providerId)).Where(j => !j.Payload.Contains("\"eventId\": null"));
        Assert.Single(fromWebhook);
    }

    [Fact]
    public async Task The_webhook_body_is_never_trusted_only_the_payment_as_mercado_pago_reports_it()
    {
        var student = await Buyer.CreateAsync(api);
        var paymentId = await student.BuyPixAsync();
        var providerId = api.Payments.ProviderIdOf(paymentId)!;

        // O corpo diz aprovado; lá o pagamento continua pendente.
        var json = $$$"""{"id":{{{Random.Shared.NextInt64(1, long.MaxValue)}}},"type":"payment","action":"payment.updated","data":{"id":"{{{providerId}}}","status":"approved"}}""";
        await api.CreateClient().SendAsync(ApiFixture.CreateMercadoPagoWebhook(providerId, json));
        await RunPaymentJobsAsync();

        Assert.Equal(1, await student.CreditsAsync());
        Assert.NotNull(await api.QueryAsync(db => db.WebhookEvents.Where(e => e.ResourceId == providerId).Select(e => e.ProcessedAt).SingleAsync()));
    }

    [Fact]
    public async Task A_refund_or_chargeback_removes_the_unused_credits_of_that_purchase()
    {
        var refunded = await Buyer.CreateAsync(api);
        var charged = await Buyer.CreateAsync(api);
        var refundedPayment = await refunded.BuyAndApproveAsync(quantity: 3);
        var chargedPayment = await charged.BuyAndApproveAsync(quantity: 2);
        // Uma das três correções já foi usada.
        await api.QueryAsync(db => db.CreditBuckets.Where(b => b.GrantKey == CreditBucket.PaymentGrantKey(refundedPayment))
            .ExecuteUpdateAsync(b => b.SetProperty(x => x.Remaining, 2)));

        api.Payments.Set(api.Payments.ProviderIdOf(refundedPayment)!, PaymentStatus.Refunded);
        api.Payments.Set(api.Payments.ProviderIdOf(chargedPayment)!, PaymentStatus.ChargedBack);
        await refunded.NotifyAsync(refundedPayment);
        await charged.NotifyAsync(chargedPayment);
        await RunPaymentJobsAsync();
        await RunPaymentJobsAsync();

        Assert.Equal(1, await refunded.CreditsAsync()); // só o teste grátis
        Assert.Equal(1, await charged.CreditsAsync());
        var reversal = Assert.Single(await api.QueryAsync(db => db.CreditLedger
            .Where(e => e.PaymentId == refundedPayment && e.Delta < 0).ToListAsync()));
        Assert.Equal(-2, reversal.Delta);
        Assert.Equal(PaymentStatus.Refunded, await api.QueryAsync(db => db.Payments.Where(p => p.Id == refundedPayment).Select(p => p.Status).SingleAsync()));
        Assert.Equal(PaymentStatus.ChargedBack, await api.QueryAsync(db => db.Payments.Where(p => p.Id == chargedPayment).Select(p => p.Status).SingleAsync()));
        Assert.Equal(1, await api.QueryAsync(db => db.AuditLog.CountAsync(a => a.Action == "payment.charged_back" && a.EntityId == chargedPayment)));
    }

    [Fact]
    public async Task A_correction_reserved_before_a_chargeback_does_not_come_back_when_the_essay_fails()
    {
        var student = await Buyer.CreateAsync(api);
        var paymentId = await student.BuyAndApproveAsync(quantity: 1);
        Assert.Equal(2, await student.CreditsAsync());

        // As duas correções ficam reservadas: a do teste grátis e a da compra.
        await student.SubmitAsync(count: 2);
        Assert.Equal(0, await student.CreditsAsync());

        // Chargeback: não há saldo para tirar, mas a compra fica revogada.
        api.Payments.Set(api.Payments.ProviderIdOf(paymentId)!, PaymentStatus.ChargedBack);
        await student.NotifyAsync(paymentId);
        await RunPaymentJobsAsync();

        // As duas redações falham depois (foto ilegível): só a correção do teste grátis volta.
        api.Transcriber.Script.Enqueue(() => FakeTranscriber.Reading(readable: false));
        api.Transcriber.Script.Enqueue(() => FakeTranscriber.Reading(readable: false));
        var runner = api.Factory.Services.GetRequiredService<IJobRunner>();
        while (await runner.RunNextAsync([JobTypes.Transcribe], CancellationToken.None))
        {
        }

        Assert.Equal(1, await student.CreditsAsync());
        var bucket = await api.QueryAsync(db => db.CreditBuckets.SingleAsync(b => b.GrantKey == CreditBucket.PaymentGrantKey(paymentId)));
        Assert.Equal(0, bucket.Remaining);
        Assert.NotNull(bucket.RevokedAt);
        var essayIds = await api.QueryAsync(db => db.Essays.Where(e => e.UserId == student.Id).Select(e => e.Id).ToListAsync());
        Assert.Equal(1, await api.QueryAsync(db => db.AuditLog.CountAsync(a => a.Action == "credit.refund_skipped" && essayIds.Contains(a.EntityId))));
    }

    [Fact]
    public async Task Simultaneous_checks_of_the_same_approval_grant_once()
    {
        var student = await Buyer.CreateAsync(api);
        var paymentId = await student.BuyPixAsync(quantity: 2);
        api.Payments.Set(api.Payments.ProviderIdOf(paymentId)!, PaymentStatus.Approved);
        await student.NotifyAsync(paymentId);
        await student.NotifyAsync(paymentId);

        var runner = api.Factory.Services.GetRequiredService<IJobRunner>();
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => runner.RunNextAsync([JobTypes.ProcessPayment], CancellationToken.None)));
        await RunPaymentJobsAsync();

        Assert.Equal(3, await student.CreditsAsync());
        Assert.Single(await api.QueryAsync(db => db.CreditLedger.Where(e => e.PaymentId == paymentId).ToListAsync()));
    }

    [Fact]
    public async Task A_pix_paid_without_a_notification_is_found_by_the_reconciliation()
    {
        var student = await Buyer.CreateAsync(api);
        var paymentId = await student.BuyPixAsync();
        await RunPaymentJobsAsync();
        api.Payments.Set(api.Payments.ProviderIdOf(paymentId)!, PaymentStatus.Approved);

        // Recém-criada, a compra ainda não entra na reconciliação.
        Assert.DoesNotContain(api.Payments.ProviderIdOf(paymentId), await RecheckAsync());

        await api.QueryAsync(db => db.Payments.Where(p => p.Id == paymentId)
            .ExecuteUpdateAsync(p => p.SetProperty(x => x.CreatedAt, x => x.CreatedAt.AddMinutes(-5))));
        Assert.Contains(api.Payments.ProviderIdOf(paymentId), await RecheckAsync());
        // Com a consulta já na fila, a próxima passada não enfileira outra.
        Assert.DoesNotContain(api.Payments.ProviderIdOf(paymentId), await RecheckAsync());
        await RunPaymentJobsAsync();

        Assert.Equal(2, await student.CreditsAsync());
    }

    [Fact]
    public async Task A_different_amount_there_grants_nothing_and_stops_for_manual_review()
    {
        var student = await Buyer.CreateAsync(api);
        var paymentId = await student.BuyPixAsync();
        api.Payments.Set(api.Payments.ProviderIdOf(paymentId)!, PaymentStatus.Approved, amountCents: 100);

        await student.NotifyAsync(paymentId);
        await RunPaymentJobsAsync();

        Assert.Equal(1, await student.CreditsAsync());
        Assert.Equal(PaymentStatus.Pending, await api.QueryAsync(db => db.Payments.Where(p => p.Id == paymentId).Select(p => p.Status).SingleAsync()));
        Assert.True(await api.QueryAsync(db => db.AuditLog.AnyAsync(a => a.Action == "payment.mismatch" && a.EntityId == paymentId)));
        // O job da criação e o do aviso param para conferência manual, sem novas tentativas.
        Assert.All(await PaymentJobsAsync(api.Payments.ProviderIdOf(paymentId)!), j => Assert.Equal(JobStatus.Dead, j.Status));
    }

    [Fact]
    public async Task A_student_only_sees_their_own_purchases()
    {
        var owner = await Buyer.CreateAsync(api);
        var other = await Buyer.CreateAsync(api);
        var paymentId = await owner.BuyPixAsync();

        var foreign = await other.Client.GetAsync($"/v1/payments/{paymentId}");
        var list = await other.Client.GetFromJsonAsync<JsonElement>("/v1/payments");
        var own = await owner.Client.GetFromJsonAsync<JsonElement>("/v1/payments");

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Empty(list.GetProperty("items").EnumerateArray());
        Assert.Equal(paymentId, Assert.Single(own.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
    }

    private static object Card() =>
        new { quantity = 1, method = "card", card = new { token = "tok_test_123", paymentMethodId = "visa", issuerId = "25", identificationType = "CPF", identificationNumber = "19119119100" } };

    /// <summary>Roda a reconciliação e devolve os pagamentos que ela pôs na fila agora.</summary>
    private async Task<List<string>> RecheckAsync()
    {
        var before = (await api.QueryAsync(db => db.Jobs.Where(j => j.Type == JobTypes.ProcessPayment).Select(j => j.Id).ToListAsync())).ToHashSet();
        await using (var scope = api.Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<RedigiAI.Application.Billing.IPaymentService>().RecheckPendingAsync(CancellationToken.None);
        }

        var jobs = await api.QueryAsync(db => db.Jobs.Where(j => j.Type == JobTypes.ProcessPayment).ToListAsync());
        return [.. jobs.Where(j => !before.Contains(j.Id)).Select(j => JsonDocument.Parse(j.Payload).RootElement.GetProperty("mpPaymentId").GetString()!)];
    }

    /// <summary>Os jobs de pagamento de um pagamento do Mercado Pago (o payload é jsonb: filtra aqui).</summary>
    private async Task<List<Job>> PaymentJobsAsync(string providerId) =>
        [.. (await api.QueryAsync(db => db.Jobs.Where(j => j.Type == JobTypes.ProcessPayment).ToListAsync()))
            .Where(j => j.Payload.Contains(providerId))];

    /// <summary>Roda todos os jobs de pagamento prontos, como o worker faria.</summary>
    private async Task RunPaymentJobsAsync()
    {
        var runner = api.Factory.Services.GetRequiredService<IJobRunner>();
        while (await runner.RunNextAsync([JobTypes.ProcessPayment], CancellationToken.None))
        {
        }
    }

    /// <summary>Um aluno cadastrado, com o teste grátis.</summary>
    private sealed class Buyer(ApiFixture api, HttpClient client, Guid id)
    {
        public HttpClient Client { get; } = client;

        public Guid Id { get; } = id;

        public static async Task<Buyer> CreateAsync(ApiFixture api, DateOnly? birthDate = null)
        {
            var client = api.CreateClient(api.CreateToken(ApiFixture.NewClerkUserId()));
            var profile = await client.PostAsJsonAsync(
                "/v1/me", new { birthDate = (birthDate ?? new DateOnly(2000, 5, 17)).ToString("yyyy-MM-dd"), termsAccepted = true });
            profile.EnsureSuccessStatusCode();
            return new Buyer(api, client, (await profile.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        }

        public Task<HttpResponseMessage> CheckoutAsync(object body, string? key = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/checkout/single") { Content = JsonContent.Create(body) };
            request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
            return Client.SendAsync(request);
        }

        public async Task<Guid> BuyPixAsync(int quantity = 1)
        {
            var response = await CheckoutAsync(new { quantity, method = "pix" });
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        }

        public async Task<Guid> BuyAndApproveAsync(int quantity)
        {
            var paymentId = await BuyPixAsync(quantity);
            api.Payments.Set(api.Payments.ProviderIdOf(paymentId)!, PaymentStatus.Approved);
            await NotifyAsync(paymentId);
            var runner = api.Factory.Services.GetRequiredService<IJobRunner>();
            while (await runner.RunNextAsync([JobTypes.ProcessPayment], CancellationToken.None))
            {
            }

            return paymentId;
        }

        /// <summary>O Mercado Pago avisa que o pagamento mudou.</summary>
        public async Task NotifyAsync(Guid paymentId)
        {
            var providerId = api.Payments.ProviderIdOf(paymentId)!;
            var json = $$$"""{"id":{{{Random.Shared.NextInt64(1, long.MaxValue)}}},"type":"payment","action":"payment.updated","data":{"id":"{{{providerId}}}"}}""";
            var response = await api.CreateClient().SendAsync(ApiFixture.CreateMercadoPagoWebhook(providerId, json, requestId: Guid.NewGuid().ToString()));
            response.EnsureSuccessStatusCode();
        }

        /// <summary>Envia redações da proposta de exemplo, como o aluno faria, reservando uma correção cada.</summary>
        public async Task SubmitAsync(int count)
        {
            var prompt = await Client.GetFromJsonAsync<JsonElement>("/v1/prompts/algoritmos-e-o-debate-publico");
            var items = new List<object>();
            for (var i = 0; i < count; i++)
            {
                var assetId = (await (await Client.PostAsync("/v1/uploads/sign", null)).Content.ReadFromJsonAsync<JsonElement>())
                    .GetProperty("assetId").GetGuid();
                var publicId = await api.QueryAsync(db => db.Assets.Where(a => a.Id == assetId).Select(a => a.PublicId).SingleAsync());
                api.Photos.Stored[publicId] = new StoredPhoto(2 * 1024 * 1024, "jpg");
                items.Add(new { promptId = prompt.GetProperty("id").GetGuid(), assetId });
            }

            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/essays/batch")
            {
                Content = JsonContent.Create(new { essays = items, aiNoticeAccepted = true }),
            };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            (await Client.SendAsync(request)).EnsureSuccessStatusCode();
        }

        public async Task<int> CreditsAsync() =>
            (await Client.GetFromJsonAsync<JsonElement>("/v1/me")).GetProperty("credits").GetProperty("total").GetInt32();
    }
}
