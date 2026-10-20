using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RedigiAI.Application.Abstractions;
using RedigiAI.Application.Billing;
using RedigiAI.Domain.Billing;
using RedigiAI.Domain.Common;
using RedigiAI.Domain.Jobs;

namespace RedigiAI.IntegrationTests;

/// <summary>
/// Plano mensal no Mercado Pago (etapa 4c): checkout transparente da assinatura (cartão tokenizado na nossa página), cobranças mensais com 12 correções que
/// não acumulam, cobrança recusada e cancelamento. O Mercado Pago é o simulado da fixture.
/// </summary>
[Collection(ApiCollection.Name)]
public class SubscriptionTests(ApiFixture api)
{
    [Fact]
    public async Task The_plan_checkout_creates_the_subscription_there_with_the_card_and_the_query_activates_it()
    {
        var student = await Subscriber.CreateAsync(api);
        var key = Guid.NewGuid().ToString();

        var first = await student.CheckoutAsync(key: key);
        var again = await student.CheckoutAsync(key: key);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var checkout = await first.Content.ReadFromJsonAsync<JsonElement>();
        var subscriptionId = checkout.GetProperty("subscriptionId").GetGuid();
        Assert.Equal("pending", checkout.GetProperty("status").GetString());
        Assert.Equal(subscriptionId, (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("subscriptionId").GetGuid());
        Assert.NotNull(api.Payments.PreapprovalIdOf(subscriptionId));
        Assert.Equal(1, await api.QueryAsync(db => db.Subscriptions.CountAsync(s => s.UserId == student.Id)));

        // Antes da consulta ao Mercado Pago, o perfil não mostra plano nem correções do plano.
        var profile = await student.ProfileAsync();
        Assert.Equal(JsonValueKind.Null, profile.GetProperty("subscription").ValueKind);
        Assert.False(profile.GetProperty("needsGuardianAuthorization").GetBoolean());

        // A consulta já está na fila: sem esperar o aviso, o plano fica ativo; as correções só vêm com a cobrança.
        await RunAsync(JobTypes.ProcessSubscription);
        profile = await student.ProfileAsync();
        Assert.Equal("active", profile.GetProperty("subscription").GetProperty("status").GetString());
        Assert.Equal("4242", profile.GetProperty("subscription").GetProperty("cardLast4").GetString());
        Assert.Equal(1, profile.GetProperty("credits").GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task A_declined_card_creates_nothing_there_and_another_card_with_the_same_key_subscribes()
    {
        var student = await Subscriber.CreateAsync(api);
        var key = Guid.NewGuid().ToString();

        var declined = await student.CheckoutAsync(key: key, cardToken: FakePaymentGateway.DeclinedCardToken);
        var other = await student.CheckoutAsync(key: key);

        Assert.Equal(HttpStatusCode.PaymentRequired, declined.StatusCode);
        Assert.Equal("PAYMENT_FAILED", (await declined.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        var subscriptionId = (await other.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("subscriptionId").GetGuid();
        Assert.NotNull(api.Payments.PreapprovalIdOf(subscriptionId));
        Assert.Equal(1, await api.QueryAsync(db => db.Subscriptions.CountAsync(s => s.UserId == student.Id)));
    }

    [Fact]
    public async Task Simultaneous_checkouts_with_different_card_tokens_create_one_subscription_there()
    {
        var student = await Subscriber.CreateAsync(api);
        var key = Guid.NewGuid().ToString();

        // Duplo clique e outra aba: cada envio do formulário traz um token novo.
        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => student.CheckoutAsync(key: i < 3 ? key : null)));

        // Quem chega no meio da criação recebe um erro que dá para repetir; nenhum cria uma segunda lá.
        Assert.All(responses, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
        Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.OK);
        foreach (var conflict in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
        {
            Assert.Equal("CHECKOUT_IN_PROGRESS", (await conflict.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        var subscriptionId = await api.QueryAsync(db => db.Subscriptions.Where(s => s.UserId == student.Id).Select(s => s.Id).SingleAsync());
        Assert.Single(api.Payments.SubscriptionsFor(subscriptionId));
        Assert.Null(await api.QueryAsync(db => db.Subscriptions.Where(s => s.Id == subscriptionId).Select(s => s.CreatingUntil).SingleAsync()));
    }

    [Fact]
    public async Task A_creation_accepted_there_without_the_answer_is_reused_by_the_next_attempt()
    {
        var student = await Subscriber.CreateAsync(api);
        var key = Guid.NewGuid().ToString();
        api.Payments.LoseNextSubscriptionResponse = true;

        var lost = await student.CheckoutAsync(key: key);
        var retry = await student.CheckoutAsync(key: key);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, lost.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var subscriptionId = (await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("subscriptionId").GetGuid();
        var there = Assert.Single(api.Payments.SubscriptionsFor(subscriptionId));
        Assert.Equal(there.ProviderId, await api.QueryAsync(db => db.Subscriptions.Where(s => s.Id == subscriptionId).Select(s => s.MpPreapprovalId).SingleAsync()));
    }

    [Fact]
    public async Task An_extra_subscription_there_with_our_reference_is_cancelled_and_its_charge_renews_nothing()
    {
        var student = await Subscriber.CreateAsync(api);
        var preapprovalId = await student.SubscribeAsync();
        await student.ChargeAsync(preapprovalId, PaymentStatus.Approved);
        var subscriptionId = await api.QueryAsync(db => db.Subscriptions.Where(s => s.MpPreapprovalId == preapprovalId).Select(s => s.Id).SingleAsync());

        // Uma segunda criação lá, com a mesma referência, cuja resposta nunca chegou.
        var orphan = await api.Payments.CreateSubscriptionAsync(
            new SubscriptionRequest(
                subscriptionId, Guid.NewGuid().ToString(), 3990, "plano", "aluna@exemplo.com", new CardToken("tok", "master", null, null, null), "https://redigiai.test/perfil"),
            CancellationToken.None);
        var charged = api.Payments.Charge(orphan.ProviderId, PaymentStatus.Approved);
        await student.NotifyAsync("payment", charged.PaymentId);
        await RunAsync(JobTypes.ProcessPayment);
        await student.NotifyAsync("subscription_preapproval", orphan.ProviderId);
        await RunAsync(JobTypes.ProcessSubscription);

        Assert.Equal(SubscriptionStatus.Cancelled, api.Payments.SubscriptionThere(orphan.ProviderId).Status);
        Assert.Equal(SubscriptionStatus.Active, api.Payments.SubscriptionThere(preapprovalId).Status);
        Assert.Equal(13, await student.CreditsAsync());
        Assert.Empty(await api.QueryAsync(db => db.Payments.Where(p => p.MpPaymentId == charged.PaymentId).ToListAsync()));
        Assert.True(await api.QueryAsync(db => db.AuditLog.AnyAsync(a => a.Action == "payment.orphan_subscription_charge" && a.EntityId == subscriptionId)));
        Assert.True(await api.QueryAsync(db => db.AuditLog.AnyAsync(a => a.Action == "subscription.orphan_cancelled" && a.EntityId == subscriptionId)));
    }

    [Fact]
    public void The_card_token_never_appears_when_the_record_is_printed()
    {
        var card = new CardToken("tok-secreto", "master", "25", "CPF", "12345678909");
        var request = new CheckoutPlanCommand("user", "key", false, new CardPaymentRequest("tok-secreto", "master", "25", "CPF", "12345678909"));

        Assert.DoesNotContain("tok-secreto", card.ToString());
        Assert.DoesNotContain("12345678909", card.ToString());
        Assert.DoesNotContain("tok-secreto", request.ToString());
        Assert.DoesNotContain("12345678909", request.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("tok en")]
    public async Task The_plan_checkout_requires_the_card_token(string? token)
    {
        var student = await Subscriber.CreateAsync(api);

        var response = await student.CheckoutAsync(cardToken: token, sendCard: token is not null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0, await api.QueryAsync(db => db.Subscriptions.CountAsync(s => s.UserId == student.Id)));
    }

    [Fact]
    public async Task A_new_checkout_reuses_the_open_one_and_simultaneous_requests_create_one_subscription()
    {
        var student = await Subscriber.CreateAsync(api);
        var key = Guid.NewGuid().ToString();

        var parallel = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => student.CheckoutAsync(key: key)));
        var otherTab = await student.CheckoutAsync();

        // Quem chega no meio da criação recebe CHECKOUT_IN_PROGRESS (409) e pode repetir; os demais, a mesma assinatura.
        Assert.All(parallel, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
        Assert.Equal(HttpStatusCode.OK, otherTab.StatusCode);
        var ids = await Task.WhenAll(parallel.Where(r => r.StatusCode == HttpStatusCode.OK).Append(otherTab).Select(async r =>
            (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("subscriptionId").GetGuid()));
        Assert.Single(ids.Distinct());
        Assert.Equal(1, await api.QueryAsync(db => db.Subscriptions.CountAsync(s => s.UserId == student.Id)));
    }

    [Fact]
    public async Task A_pending_checkout_can_be_cancelled_here_and_there()
    {
        var student = await Subscriber.CreateAsync(api);
        var subscriptionId = (await (await student.CheckoutAsync()).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("subscriptionId").GetGuid();

        var response = await student.Client.PostAsync("/v1/subscription/cancel", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(SubscriptionStatus.Cancelled, api.Payments.SubscriptionThere(api.Payments.PreapprovalIdOf(subscriptionId)!).Status);
        Assert.Equal(SubscriptionStatus.Cancelled, await api.QueryAsync(db => db.Subscriptions.Where(s => s.Id == subscriptionId).Select(s => s.Status).SingleAsync()));
    }

    [Fact]
    public async Task A_second_subscription_authorized_there_is_cancelled_and_its_charge_gives_nothing()
    {
        var student = await Subscriber.CreateAsync(api);
        var first = await student.SubscribeAsync();
        await student.ChargeAsync(first, PaymentStatus.Approved);

        // Um checkout antigo, de antes do plano, concluído depois.
        var duplicate = Subscription.Request(student.Id, Guid.NewGuid().ToString(), null, DateTimeOffset.UtcNow);
        await api.QueryAsync(async db =>
        {
            db.Subscriptions.Add(duplicate);
            return await db.SaveChangesAsync();
        });
        var created = await api.Payments.CreateSubscriptionAsync(
            new SubscriptionRequest(
                duplicate.Id, Guid.NewGuid().ToString(), 3990, "plano", "aluna@exemplo.com", new CardToken("tok", "master", null, null, null), "https://redigiai.test/perfil"),
            CancellationToken.None);
        await api.QueryAsync(db => db.Subscriptions.Where(s => s.Id == duplicate.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.MpPreapprovalId, created.ProviderId)));
        api.Payments.SetSubscription(created.ProviderId, SubscriptionStatus.Active, DateTimeOffset.UtcNow.AddMonths(1));

        await student.NotifyAsync("subscription_preapproval", created.ProviderId);
        await RunAsync(JobTypes.ProcessSubscription);

        Assert.Equal(SubscriptionStatus.Cancelled, api.Payments.SubscriptionThere(created.ProviderId).Status);
        Assert.Equal(SubscriptionStatus.Cancelled, await api.QueryAsync(db => db.Subscriptions.Where(s => s.Id == duplicate.Id).Select(s => s.Status).SingleAsync()));

        // Uma cobrança dela que passou antes do cancelamento não renova: fica para estorno manual.
        var charged = await student.ChargeAsync(created.ProviderId, PaymentStatus.Approved);
        Assert.Equal(13, await student.CreditsAsync());
        var chargeId = await api.QueryAsync(db => db.Payments.Where(p => p.MpPaymentId == charged).Select(p => p.Id).SingleAsync());
        Assert.True(await api.QueryAsync(db => db.AuditLog.AnyAsync(a => a.Action == "payment.duplicate_subscription" && a.EntityId == chargeId)));
    }

    [Fact]
    public async Task Two_charges_approved_at_the_same_time_still_leave_12_corrections()
    {
        var student = await Subscriber.CreateAsync(api);
        var preapprovalId = await student.SubscribeAsync();
        var one = api.Payments.Charge(preapprovalId, PaymentStatus.Approved);
        var two = api.Payments.Charge(preapprovalId, PaymentStatus.Approved);
        await student.NotifyAsync("subscription_authorized_payment", one.ChargeId);
        await student.NotifyAsync("subscription_authorized_payment", two.ChargeId);
        await RunAsync(JobTypes.ProcessSubscription);

        var runner = api.Factory.Services.GetRequiredService<IJobRunner>();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => runner.RunNextAsync([JobTypes.ProcessPayment], CancellationToken.None)));
        await RunAsync(JobTypes.ProcessPayment);

        Assert.Equal(13, await student.CreditsAsync());
    }

    [Fact]
    public async Task A_charge_whose_notice_was_lost_is_recorded_from_the_payment_notice()
    {
        var student = await Subscriber.CreateAsync(api);
        var preapprovalId = await student.SubscribeAsync();
        var (_, paymentId) = api.Payments.Charge(preapprovalId, PaymentStatus.Approved);

        // Só o aviso do pagamento chega.
        await student.NotifyAsync("payment", paymentId);
        await RunAsync(JobTypes.ProcessPayment);

        Assert.Equal(13, await student.CreditsAsync());
        var payment = await api.QueryAsync(db => db.Payments.SingleAsync(p => p.MpPaymentId == paymentId));
        Assert.Equal(PaymentKind.Plan, payment.Kind);
    }

    [Fact]
    public async Task A_refused_charge_approved_on_a_later_attempt_renews_the_plan()
    {
        var student = await Subscriber.CreateAsync(api);
        var preapprovalId = await student.SubscribeAsync();
        var refused = await student.ChargeAsync(preapprovalId, PaymentStatus.Rejected);
        Assert.Equal("paused", (await student.ProfileAsync()).GetProperty("subscription").GetProperty("status").GetString());

        // A nova tentativa lá aprova o mesmo pagamento.
        api.Payments.Set(refused, PaymentStatus.Approved);
        await student.NotifyAsync("payment", refused);
        await RunAsync(JobTypes.ProcessPayment);

        Assert.Equal(13, await student.CreditsAsync());
        Assert.Equal("active", (await student.ProfileAsync()).GetProperty("subscription").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Buyers_aged_16_or_17_must_declare_the_guardian_authorization_for_the_plan()
    {
        var minor = await Subscriber.CreateAsync(api, birthDate: DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-16).AddDays(-10));
        // O perfil avisa a tela de compra; a idade é sempre calculada aqui.
        Assert.True((await minor.ProfileAsync()).GetProperty("needsGuardianAuthorization").GetBoolean());

        var refused = await minor.CheckoutAsync();
        var accepted = await minor.CheckoutAsync(guardianAuthorized: true);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("GUARDIAN_AUTHORIZATION_REQUIRED", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.NotNull(await api.QueryAsync(db => db.Subscriptions.Where(s => s.UserId == minor.Id).Select(s => s.GuardianAuthorizedAt).SingleAsync()));
    }

    [Fact]
    public async Task Each_approved_charge_gives_12_corrections_that_reset_at_the_next_one()
    {
        var student = await Subscriber.CreateAsync(api);
        var preapprovalId = await student.SubscribeAsync(nextChargeAt: DateTimeOffset.UtcNow.AddMonths(1));
        Assert.Equal("active", (await student.ProfileAsync()).GetProperty("subscription").GetProperty("status").GetString());

        var first = await student.ChargeAsync(preapprovalId, PaymentStatus.Approved);
        Assert.Equal(13, await student.CreditsAsync()); // 12 do plano + o teste grátis
        var payment = await api.QueryAsync(db => db.Payments.SingleAsync(p => p.MpPaymentId == first));
        Assert.Equal((PaymentKind.Plan, PaymentStatus.Approved, 3990L), (payment.Kind, payment.Status, payment.AmountCents));
        var bucket = await api.QueryAsync(db => db.CreditBuckets.SingleAsync(b => b.GrantKey == CreditBucket.PaymentGrantKey(payment.Id)));
        Assert.Equal(CreditSource.Plan, bucket.Source);
        // O banco guarda microssegundos.
        Assert.Equal(api.Payments.SubscriptionThere(preapprovalId).NextChargeAt!.Value, bucket.ExpiresAt!.Value, TimeSpan.FromMilliseconds(1));

        // Usou 5; o mês seguinte zera o que sobrou e volta a 12.
        await api.QueryAsync(db => db.CreditBuckets.Where(b => b.Id == bucket.Id).ExecuteUpdateAsync(b => b.SetProperty(x => x.Remaining, 7)));
        api.Payments.SetSubscription(preapprovalId, SubscriptionStatus.Active, nextChargeAt: DateTimeOffset.UtcNow.AddMonths(2));
        await student.ChargeAsync(preapprovalId, PaymentStatus.Approved);

        Assert.Equal(13, await student.CreditsAsync());
        Assert.Equal(0, (await api.QueryAsync(db => db.CreditBuckets.SingleAsync(b => b.Id == bucket.Id))).Remaining);
    }

    [Fact]
    public async Task The_same_charge_notified_twice_becomes_one_purchase()
    {
        var student = await Subscriber.CreateAsync(api);
        var preapprovalId = await student.SubscribeAsync();
        var (chargeId, paymentId) = api.Payments.Charge(preapprovalId, PaymentStatus.Approved);

        await student.NotifyAsync("subscription_authorized_payment", chargeId);
        await student.NotifyAsync("subscription_authorized_payment", chargeId);
        await RunAsync(JobTypes.ProcessSubscription);
        await RunAsync(JobTypes.ProcessPayment);

        Assert.Single(await api.QueryAsync(db => db.Payments.Where(p => p.MpPaymentId == paymentId).ToListAsync()));
        Assert.Equal(13, await student.CreditsAsync());
    }

    [Fact]
    public async Task A_refused_charge_pauses_the_plan_and_the_next_approved_one_resumes_it()
    {
        await SetEmailSendingAsync(true);
        try
        {
            var student = await Subscriber.CreateAsync(api);
            var preapprovalId = await student.SubscribeAsync(cardLast4: "4242");
            await student.ChargeAsync(preapprovalId, PaymentStatus.Approved);

            var refused = await student.ChargeAsync(preapprovalId, PaymentStatus.Rejected);

            Assert.Equal("paused", (await student.ProfileAsync()).GetProperty("subscription").GetProperty("status").GetString());
            // As correções que o aluno já tem continuam.
            Assert.Equal(13, await student.CreditsAsync());
            await RunAsync(JobTypes.SendEmail);
            var refusedId = await api.QueryAsync(db => db.Payments.Where(p => p.MpPaymentId == refused).Select(p => p.Id).SingleAsync());
            var notice = Assert.Single(api.Emails.For(refusedId));
            Assert.Equal("subscription_payment_failed", notice.Template);
            Assert.Contains("no cartão com final 4242", notice.Text);

            await student.ChargeAsync(preapprovalId, PaymentStatus.Approved);
            Assert.Equal("active", (await student.ProfileAsync()).GetProperty("subscription").GetProperty("status").GetString());
        }
        finally
        {
            await SetEmailSendingAsync(false);
        }
    }

    [Fact]
    public async Task A_student_with_an_open_plan_cannot_subscribe_again()
    {
        var student = await Subscriber.CreateAsync(api);
        await student.SubscribeAsync();

        var again = await student.CheckoutAsync();

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("PLAN_ALREADY_ACTIVE", (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Cancelling_stops_the_renewal_there_and_keeps_the_corrections_until_the_end_of_the_cycle()
    {
        var student = await Subscriber.CreateAsync(api);
        var preapprovalId = await student.SubscribeAsync(nextChargeAt: DateTimeOffset.UtcNow.AddDays(20));
        await student.ChargeAsync(preapprovalId, PaymentStatus.Approved);

        var response = await student.Client.PostAsync("/v1/subscription/cancel", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("cancelled", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal(SubscriptionStatus.Cancelled, api.Payments.SubscriptionThere(preapprovalId).Status);
        Assert.Equal(13, await student.CreditsAsync());
        Assert.NotNull(await api.QueryAsync(db => db.Subscriptions.Where(s => s.MpPreapprovalId == preapprovalId).Select(s => s.CancelledAt).SingleAsync()));

        // Sem plano aberto, cancelar de novo é 404; e o aluno pode assinar outra vez.
        Assert.Equal(HttpStatusCode.NotFound, (await student.Client.PostAsync("/v1/subscription/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await student.CheckoutAsync()).StatusCode);
    }

    [Fact]
    public async Task A_subscription_authorized_without_a_notification_is_found_by_the_reconciliation()
    {
        var student = await Subscriber.CreateAsync(api);
        var subscriptionId = (await (await student.CheckoutAsync()).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("subscriptionId").GetGuid();
        var preapprovalId = api.Payments.PreapprovalIdOf(subscriptionId)!;
        // A consulta posta na criação se perdeu, e o aviso nunca chegou.
        var lost = (await api.QueryAsync(db => db.Jobs.Where(j => j.Type == JobTypes.ProcessSubscription).ToListAsync()))
            .Where(j => j.Payload.Contains(preapprovalId)).Select(j => j.Id).ToList();
        await api.QueryAsync(db => db.Jobs.Where(j => lost.Contains(j.Id)).ExecuteDeleteAsync());
        await api.QueryAsync(db => db.Subscriptions.Where(s => s.Id == subscriptionId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, x => x.CreatedAt.AddMinutes(-5))));

        await using (var scope = api.Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPaymentService>().RecheckPendingAsync(CancellationToken.None);
        }

        await RunAsync(JobTypes.ProcessSubscription);
        Assert.Equal("active", (await student.ProfileAsync()).GetProperty("subscription").GetProperty("status").GetString());
    }

    [Fact]
    public async Task When_mercado_pago_is_down_nothing_is_created_there_and_the_same_key_tries_again()
    {
        var student = await Subscriber.CreateAsync(api);
        var key = Guid.NewGuid().ToString();
        api.Payments.Failures.Enqueue(Errors.PaymentUnavailable());

        var down = await student.CheckoutAsync(key: key);
        var retry = await student.CheckoutAsync(key: key);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(1, await api.QueryAsync(db => db.Subscriptions.CountAsync(s => s.UserId == student.Id)));
    }

    private Task<int> SetEmailSendingAsync(bool enabled) =>
        api.QueryAsync(db => db.FeatureFlags
            .Where(f => f.Key == FeatureFlag.EmailSending)
            .ExecuteUpdateAsync(f => f.SetProperty(x => x.Enabled, enabled)));

    private async Task RunAsync(string type)
    {
        var runner = api.Factory.Services.GetRequiredService<IJobRunner>();
        while (await runner.RunNextAsync([type], CancellationToken.None))
        {
        }
    }

    /// <summary>Um aluno cadastrado, com o teste grátis, e os passos do plano como o Mercado Pago os avisa.</summary>
    private sealed class Subscriber(ApiFixture api, HttpClient client, Guid id)
    {
        public HttpClient Client { get; } = client;

        public Guid Id { get; } = id;

        public static async Task<Subscriber> CreateAsync(ApiFixture api, DateOnly? birthDate = null)
        {
            var client = api.CreateClient(api.CreateToken(ApiFixture.NewClerkUserId()));
            var profile = await client.PostAsJsonAsync(
                "/v1/me", new { birthDate = (birthDate ?? new DateOnly(2000, 5, 17)).ToString("yyyy-MM-dd"), termsAccepted = true });
            profile.EnsureSuccessStatusCode();
            return new Subscriber(api, client, (await profile.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        }

        /// <param name="cardToken">O token que o formulário do Mercado Pago gera no navegador; um novo a cada envio.</param>
        public Task<HttpResponseMessage> CheckoutAsync(
            string? key = null, bool guardianAuthorized = false, string? cardToken = null, bool sendCard = true)
        {
            var card = sendCard ? new { token = cardToken ?? $"tok{Guid.NewGuid():N}", paymentMethodId = "master" } : null;
            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/checkout/plan") { Content = JsonContent.Create(new { guardianAuthorized, card }) };
            request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
            return Client.SendAsync(request);
        }

        /// <summary>Assina com o cartão, e o aviso da assinatura autorizada lá chega.</summary>
        /// <returns>O id da assinatura no Mercado Pago.</returns>
        public async Task<string> SubscribeAsync(DateTimeOffset? nextChargeAt = null, string? cardLast4 = null)
        {
            var checkout = await (await CheckoutAsync()).Content.ReadFromJsonAsync<JsonElement>();
            var preapprovalId = api.Payments.PreapprovalIdOf(checkout.GetProperty("subscriptionId").GetGuid())!;
            api.Payments.SetSubscription(preapprovalId, SubscriptionStatus.Active, nextChargeAt ?? DateTimeOffset.UtcNow.AddMonths(1), cardLast4);
            await NotifyAsync("subscription_preapproval", preapprovalId);
            await RunJobsAsync();
            return preapprovalId;
        }

        /// <summary>Uma cobrança mensal lá, avisada e processada.</summary>
        /// <returns>O id do pagamento no Mercado Pago.</returns>
        public async Task<string> ChargeAsync(string preapprovalId, PaymentStatus status)
        {
            var (chargeId, paymentId) = api.Payments.Charge(preapprovalId, status);
            await NotifyAsync("subscription_authorized_payment", chargeId);
            await RunJobsAsync();
            return paymentId;
        }

        public async Task NotifyAsync(string type, string resourceId)
        {
            var json = $$$"""{"id":{{{Random.Shared.NextInt64(1, long.MaxValue)}}},"type":"{{{type}}}","action":"updated","data":{"id":"{{{resourceId}}}"}}""";
            var response = await api.CreateClient().SendAsync(
                ApiFixture.CreateMercadoPagoWebhook(resourceId, json, type: type, requestId: Guid.NewGuid().ToString()));
            response.EnsureSuccessStatusCode();
        }

        public async Task<JsonElement> ProfileAsync() => await Client.GetFromJsonAsync<JsonElement>("/v1/me");

        public async Task<int> CreditsAsync() =>
            (await ProfileAsync()).GetProperty("credits").GetProperty("total").GetInt32();

        private async Task RunJobsAsync()
        {
            var runner = api.Factory.Services.GetRequiredService<IJobRunner>();
            while (await runner.RunNextAsync([JobTypes.ProcessSubscription, JobTypes.ProcessPayment], CancellationToken.None))
            {
            }
        }
    }
}
