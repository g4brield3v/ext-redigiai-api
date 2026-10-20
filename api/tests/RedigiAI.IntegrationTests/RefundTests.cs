using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Billing;
using RedigiAI.Domain.Jobs;

namespace RedigiAI.IntegrationTests;

/// <summary>
/// Direito de arrependimento (etapa 4d): em até 7 dias da compra aprovada, saem as correções não usadas e o valor delas
/// volta pelo Mercado Pago (o simulado da fixture).
/// </summary>
[Collection(ApiCollection.Name)]
public class RefundTests(ApiFixture api)
{
    [Fact]
    public async Task Desisting_from_a_single_purchase_returns_16_reais_per_unused_correction()
    {
        var student = await Student.CreateAsync(api);
        var paymentId = await student.BuyApprovedAsync(quantity: 3);
        await student.UseAsync(paymentId, used: 1);

        var response = await student.RequestRefundAsync(paymentId, reason: "Mudei de ideia.");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var refund = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3200, refund.GetProperty("amountCents").GetInt64());
        Assert.Equal(2, refund.GetProperty("creditsRemoved").GetInt32());
        Assert.Equal("requested", refund.GetProperty("status").GetString());
        Assert.Equal(1, await student.CreditsAsync()); // só o teste grátis
        var reversal = Assert.Single(await api.QueryAsync(db => db.CreditLedger.Where(e => e.PaymentId == paymentId && e.Delta < 0).ToListAsync()));
        Assert.Equal(-2, reversal.Delta);

        await RunAsync(JobTypes.ProcessRefund);

        var refundId = refund.GetProperty("id").GetGuid();
        Assert.Equal((api.Payments.ProviderIdOf(paymentId)!, 3200L), api.Payments.Refunds[refundId]);
        var stored = await api.QueryAsync(db => db.RefundRequests.SingleAsync(r => r.Id == refundId));
        Assert.Equal(RefundStatus.Paid, stored.Status);
        Assert.Equal("Mudei de ideia.", stored.Reason);

        // Pedir de novo devolve o mesmo pedido; a compra deixa de aparecer como "desistível".
        var again = await (await student.RequestRefundAsync(paymentId)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(refundId, again.GetProperty("id").GetGuid());
        var payment = await student.Client.GetFromJsonAsync<JsonElement>($"/v1/payments/{paymentId}");
        Assert.False(payment.GetProperty("refundable").GetBoolean());
    }

    [Fact]
    public async Task The_refund_is_not_made_when_mercado_pago_shows_the_purchase_disputed()
    {
        var student = await Student.CreateAsync(api);
        var paymentId = await student.BuyApprovedAsync(quantity: 2);
        var refund = await (await student.RequestRefundAsync(paymentId)).Content.ReadFromJsonAsync<JsonElement>();
        var refundId = refund.GetProperty("id").GetGuid();

        // Antes de o job rodar, o aluno contestou a compra no banco do cartão.
        api.Payments.Set(api.Payments.ProviderIdOf(paymentId)!, PaymentStatus.ChargedBack);
        await RunAsync(JobTypes.ProcessRefund);

        Assert.False(api.Payments.Refunds.ContainsKey(refundId));
        Assert.Equal(RefundStatus.Requested, await api.QueryAsync(db => db.RefundRequests.Where(r => r.Id == refundId).Select(r => r.Status).SingleAsync()));
        Assert.True(await api.QueryAsync(db => db.AuditLog.AnyAsync(a => a.Action == "refund.blocked" && a.EntityId == refundId)));
    }

    [Fact]
    public async Task A_previous_partial_refund_lowers_what_is_returned()
    {
        var student = await Student.CreateAsync(api);
        var paymentId = await student.BuyApprovedAsync(quantity: 5);
        // R$ 70 já estornados pelo painel: 4 correções saem, e R$ 6 sobram sem fechar uma quinta.
        await api.QueryAsync(db => db.Payments.Where(p => p.Id == paymentId).ExecuteUpdateAsync(p => p.SetProperty(x => x.RefundedCents, 7000)));

        var refund = await (await student.RequestRefundAsync(paymentId)).Content.ReadFromJsonAsync<JsonElement>();

        // As 5 sem uso valeriam R$ 80, mas da compra de R$ 80 só restam R$ 10 para devolver.
        Assert.Equal(1000, refund.GetProperty("amountCents").GetInt64());
    }

    [Fact]
    public async Task The_reason_refuses_invisible_formatting_characters()
    {
        var student = await Student.CreateAsync(api);
        var paymentId = await student.BuyApprovedAsync(quantity: 1);

        var response = await student.RequestRefundAsync(paymentId, reason: "texto\u202Einvertido");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(2, await student.CreditsAsync());
    }

    [Fact]
    public async Task Simultaneous_requests_make_one_refund()
    {
        var student = await Student.CreateAsync(api);
        var paymentId = await student.BuyApprovedAsync(quantity: 2);

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => student.RequestRefundAsync(paymentId)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Single(await api.QueryAsync(db => db.RefundRequests.Where(r => r.PaymentId == paymentId).ToListAsync()));
        Assert.Single(await api.QueryAsync(db => db.CreditLedger.Where(e => e.PaymentId == paymentId && e.Delta < 0).ToListAsync()));
        Assert.Equal(1, await student.CreditsAsync());
    }

    [Fact]
    public async Task After_7_days_the_purchase_can_no_longer_be_refunded()
    {
        var student = await Student.CreateAsync(api);
        var paymentId = await student.BuyApprovedAsync(quantity: 1);
        await api.QueryAsync(db => db.Payments.Where(p => p.Id == paymentId)
            .ExecuteUpdateAsync(p => p.SetProperty(x => x.ApprovedAt, x => x.ApprovedAt!.Value.AddDays(-7).AddMinutes(-1))));

        var response = await student.RequestRefundAsync(paymentId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("REFUND_WINDOW_EXPIRED", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(2, await student.CreditsAsync());
    }

    [Fact]
    public async Task A_purchase_whose_corrections_were_all_used_has_nothing_to_return()
    {
        var student = await Student.CreateAsync(api);
        var paymentId = await student.BuyApprovedAsync(quantity: 2);
        await student.UseAsync(paymentId, used: 2);

        var response = await student.RequestRefundAsync(paymentId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("NOTHING_TO_REFUND", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Empty(await api.QueryAsync(db => db.RefundRequests.Where(r => r.PaymentId == paymentId).ToListAsync()));
    }

    [Fact]
    public async Task A_student_cannot_desist_from_someone_elses_purchase()
    {
        var owner = await Student.CreateAsync(api);
        var other = await Student.CreateAsync(api);
        var paymentId = await owner.BuyApprovedAsync(quantity: 1);

        var response = await other.RequestRefundAsync(paymentId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(2, await owner.CreditsAsync());
    }

    [Fact]
    public async Task Desisting_from_the_plan_returns_39_90_minus_3_33_per_used_correction_and_cancels_it()
    {
        var student = await Student.CreateAsync(api);
        var (paymentId, preapprovalId) = await student.SubscribeAndChargeAsync();
        await student.UseAsync(paymentId, used: 2);

        var refund = await (await student.RequestRefundAsync(paymentId)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(3324, refund.GetProperty("amountCents").GetInt64());
        Assert.Equal(10, refund.GetProperty("creditsRemoved").GetInt32());
        Assert.Equal(1, await student.CreditsAsync());
        Assert.Equal(SubscriptionStatus.Cancelled, api.Payments.SubscriptionThere(preapprovalId).Status);
        var profile = await student.Client.GetFromJsonAsync<JsonElement>("/v1/me");
        Assert.Equal("cancelled", profile.GetProperty("subscription").GetProperty("status").GetString());
    }

    private async Task RunAsync(string type)
    {
        var runner = api.Factory.Services.GetRequiredService<IJobRunner>();
        while (await runner.RunNextAsync([type], CancellationToken.None))
        {
        }
    }

    private sealed class Student(ApiFixture api, HttpClient client)
    {
        public HttpClient Client { get; } = client;

        public static async Task<Student> CreateAsync(ApiFixture api)
        {
            var client = api.CreateClient(api.CreateToken(ApiFixture.NewClerkUserId()));
            (await client.PostAsJsonAsync("/v1/me", new { birthDate = "2000-05-17", termsAccepted = true })).EnsureSuccessStatusCode();
            return new Student(api, client);
        }

        /// <summary>Compra avulsa por Pix, paga lá e confirmada pelo job.</summary>
        public async Task<Guid> BuyApprovedAsync(int quantity)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/checkout/single") { Content = JsonContent.Create(new { quantity, method = "pix" }) };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            var paymentId = (await (await Client.SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            var providerId = api.Payments.ProviderIdOf(paymentId)!;
            api.Payments.Set(providerId, PaymentStatus.Approved);
            await NotifyAsync("payment", providerId);
            await RunJobsAsync();
            return paymentId;
        }

        /// <summary>Assina o plano, o cartão é autorizado e a primeira cobrança é aprovada.</summary>
        public async Task<(Guid PaymentId, string PreapprovalId)> SubscribeAndChargeAsync()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/checkout/plan")
            {
                Content = JsonContent.Create(new { card = new { token = $"tok{Guid.NewGuid():N}", paymentMethodId = "master" } }),
            };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            var subscriptionId = (await (await Client.SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("subscriptionId").GetGuid();
            var preapprovalId = api.Payments.PreapprovalIdOf(subscriptionId)!;
            api.Payments.SetSubscription(preapprovalId, SubscriptionStatus.Active, DateTimeOffset.UtcNow.AddMonths(1));
            await NotifyAsync("subscription_preapproval", preapprovalId);
            var (chargeId, mpPaymentId) = api.Payments.Charge(preapprovalId, PaymentStatus.Approved);
            await NotifyAsync("subscription_authorized_payment", chargeId);
            await RunJobsAsync();
            var paymentId = await api.QueryAsync(db => db.Payments.Where(p => p.MpPaymentId == mpPaymentId).Select(p => p.Id).SingleAsync());
            return (paymentId, preapprovalId);
        }

        /// <summary>Correções da compra já usadas (só o saldo do bucket: o envio da redação não importa aqui).</summary>
        public Task<int> UseAsync(Guid paymentId, int used) =>
            api.QueryAsync(db => db.CreditBuckets.Where(b => b.GrantKey == CreditBucket.PaymentGrantKey(paymentId))
                .ExecuteUpdateAsync(b => b.SetProperty(x => x.Remaining, x => x.Remaining - used)));

        public Task<HttpResponseMessage> RequestRefundAsync(Guid paymentId, string? reason = null) =>
            Client.PostAsJsonAsync($"/v1/payments/{paymentId}/refund-request", new { reason });

        public async Task<int> CreditsAsync() =>
            (await Client.GetFromJsonAsync<JsonElement>("/v1/me")).GetProperty("credits").GetProperty("total").GetInt32();

        private async Task NotifyAsync(string type, string resourceId)
        {
            var json = $$$"""{"id":{{{Random.Shared.NextInt64(1, long.MaxValue)}}},"type":"{{{type}}}","action":"updated","data":{"id":"{{{resourceId}}}"}}""";
            (await api.CreateClient().SendAsync(
                ApiFixture.CreateMercadoPagoWebhook(resourceId, json, type: type, requestId: Guid.NewGuid().ToString()))).EnsureSuccessStatusCode();
        }

        private async Task RunJobsAsync()
        {
            var runner = api.Factory.Services.GetRequiredService<IJobRunner>();
            while (await runner.RunNextAsync([JobTypes.ProcessSubscription, JobTypes.ProcessPayment], CancellationToken.None))
            {
            }
        }
    }
}
