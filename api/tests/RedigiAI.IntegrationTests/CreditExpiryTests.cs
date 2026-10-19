using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RedigiAI.Application.Billing;
using RedigiAI.Domain.Billing;
using RedigiAI.Infrastructure.Persistence;

namespace RedigiAI.IntegrationTests;

/// <summary>
/// Vencimento das correções e a assinatura no perfil (etapa 4). As do plano mensal vencem na data da cobrança
/// seguinte, sem acumular; o relógio é o do teste.
/// </summary>
[Collection(ApiCollection.Name)]
public class CreditExpiryTests(ApiFixture api)
{
    // As datas ficam longe de hoje para não esbarrar nos créditos dos outros testes.
    private static DateTimeOffset At(int year, int month, int day, int hour = 0, int minute = 0) =>
        new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.FromHours(-3)).ToUniversalTime();

    [Fact]
    public async Task At_the_next_billing_the_unused_plan_credits_expire_once_with_a_negative_entry()
    {
        await using var app = TimedApi.Create(api);
        app.Clock.Now = At(2031, 7, 15, 10);
        var student = await app.StudentAsync();
        // Assinou dia 15: as 12 correções do ciclo vencem na cobrança de 15/08.
        var plan = await app.GrantAsync(student, CreditSource.Plan, 12, At(2031, 8, 15, 10));
        var next = await app.GrantAsync(student, CreditSource.Plan, 12, At(2031, 9, 15, 10));
        var single = await app.GrantAsync(student, CreditSource.Single, 2, expiresAt: null);

        // Um minuto antes da cobrança nada vence.
        app.Clock.Now = At(2031, 8, 15, 9, 59);
        await app.ExpireAsync();
        Assert.Equal(12, (await app.BucketAsync(plan)).Remaining);

        app.Clock.Now = At(2031, 8, 15, 10, 4);
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => app.ExpireAsync()));
        await app.ExpireAsync();

        Assert.Equal(0, (await app.BucketAsync(plan)).Remaining);
        var expiry = Assert.Single(await api.QueryAsync(db => db.CreditLedger
            .Where(e => e.BucketId == plan && e.Delta < 0 && e.EssayId == null).ToListAsync()));
        Assert.Equal(-12, expiry.Delta);
        Assert.Equal(CreditSource.Plan, expiry.Source);
        Assert.Equal(1, await api.QueryAsync(db => db.AuditLog.CountAsync(a => a.Action == "credit.expired" && a.EntityId == plan)));

        // O que não venceu fica como estava: o novo ciclo e as avulsas.
        Assert.Equal(12, (await app.BucketAsync(next)).Remaining);
        Assert.Equal(2, (await app.BucketAsync(single)).Remaining);

        // O ledger continua fechando com o saldo de cada bucket.
        foreach (var bucketId in new[] { plan, next, single })
        {
            var sum = await api.QueryAsync(db => db.CreditLedger.Where(e => e.BucketId == bucketId).SumAsync(e => e.Delta));
            Assert.Equal((await app.BucketAsync(bucketId)).Remaining, sum);
        }
    }

    [Fact]
    public async Task The_renewal_resets_the_plan_to_12_whatever_was_left_and_whenever_it_arrives()
    {
        await using var app = TimedApi.Create(api);
        app.Clock.Now = At(2032, 2, 15, 10);
        var student = await app.StudentAsync();
        var first = await app.RenewAsync(student, cycleEnd: At(2032, 3, 15, 10));
        // Usou 7 das 12.
        await api.QueryAsync(db => db.CreditBuckets.Where(b => b.Id == first)
            .ExecuteUpdateAsync(b => b.SetProperty(x => x.Remaining, 5)));

        // A renovação chega um dia antes do fim do ciclo: o saldo do plano volta a 12, não a 17.
        app.Clock.Now = At(2032, 3, 14, 10);
        var second = await app.RenewAsync(student, cycleEnd: At(2032, 4, 14, 10));

        var profile = await student.Client.GetFromJsonAsync<JsonElement>("/v1/me");
        var plan = profile.GetProperty("credits").GetProperty("buckets").EnumerateArray()
            .Where(b => b.GetProperty("source").GetString() == "plan").ToList();
        Assert.Equal(12, Assert.Single(plan).GetProperty("remaining").GetInt32());
        var closed = await app.BucketAsync(first);
        Assert.Equal(0, closed.Remaining);
        Assert.Equal(At(2032, 3, 14, 10), closed.ExpiresAt);
        var reset = Assert.Single(await api.QueryAsync(db => db.CreditLedger
            .Where(e => e.BucketId == first && e.Delta < 0).ToListAsync()));
        Assert.Equal(-5, reset.Delta);
        Assert.Equal(12, (await app.BucketAsync(second)).Remaining);

        // Uma correção reservada no ciclo anterior não volta para ele: o bucket encerrado não aceita devolução.
        Assert.False(await app.TryCreditBackAsync(first, At(2032, 3, 14, 11)));
    }

    [Fact]
    public async Task A_student_has_at_most_one_open_subscription()
    {
        await using var app = TimedApi.Create(api);
        app.Clock.Now = At(2031, 12, 1, 10);
        var student = await app.StudentAsync();
        await app.SubscribeAsync(student);

        await Assert.ThrowsAsync<DbUpdateException>(() => app.SubscribeAsync(student));
    }

    [Fact]
    public async Task A_refund_never_lands_in_a_bucket_that_already_expired()
    {
        await using var app = TimedApi.Create(api);
        app.Clock.Now = At(2031, 12, 20, 10);
        var student = await app.StudentAsync();
        var plan = await app.GrantAsync(student, CreditSource.Plan, 12, At(2032, 1, 20, 10));
        await api.QueryAsync(db => db.CreditBuckets.Where(b => b.Id == plan)
            .ExecuteUpdateAsync(b => b.SetProperty(x => x.Remaining, 11)));

        // Um processo decide devolver depois da cobrança: o bucket vencido não recebe nada.
        Assert.False(await app.TryCreditBackAsync(plan, At(2032, 1, 20, 10, 1)));
        Assert.True(await app.TryCreditBackAsync(plan, At(2032, 1, 20, 9, 59)));
        Assert.Equal(12, (await app.BucketAsync(plan)).Remaining);
    }

    [Fact]
    public async Task The_profile_shows_the_subscription_and_the_plan_credits_with_their_expiry()
    {
        await using var app = TimedApi.Create(api);
        app.Clock.Now = At(2031, 10, 6, 9);
        var student = await app.StudentAsync();
        var newcomer = await app.StudentAsync();
        await app.SubscribeAsync(student, nextBillingAt: At(2031, 11, 6, 9), cardLast4: "4242");
        await app.GrantAsync(student, CreditSource.Plan, 12, At(2031, 11, 6, 9));

        var profile = await student.Client.GetFromJsonAsync<JsonElement>("/v1/me");

        var subscription = profile.GetProperty("subscription");
        Assert.Equal("active", subscription.GetProperty("status").GetString());
        Assert.Equal(At(2031, 11, 6, 9), subscription.GetProperty("nextBillingAt").GetDateTimeOffset());
        Assert.Equal("4242", subscription.GetProperty("cardLast4").GetString());
        var credits = profile.GetProperty("credits");
        Assert.Equal(13, credits.GetProperty("total").GetInt32()); // 12 do plano + o teste grátis
        var planBucket = credits.GetProperty("buckets").EnumerateArray().Single(b => b.GetProperty("source").GetString() == "plan");
        Assert.Equal(At(2031, 11, 6, 9), planBucket.GetProperty("expiresAt").GetDateTimeOffset());

        var withoutPlan = await newcomer.Client.GetFromJsonAsync<JsonElement>("/v1/me");
        Assert.Equal(JsonValueKind.Null, withoutPlan.GetProperty("subscription").ValueKind);
    }

    private sealed class TestClock : TimeProvider
    {
        // O PostgreSQL guarda microssegundos: o relógio do teste não usa frações menores.
        public DateTimeOffset Now { get; set; } = new(2031, 1, 1, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed record Student(Guid Id, HttpClient Client);

    /// <summary>A API com o relógio do teste, mais atalhos para assinar, conceder e vencer.</summary>
    private sealed class TimedApi(ApiFixture api, WebApplicationFactory<Program> factory, TestClock clock) : IAsyncDisposable
    {
        public TestClock Clock { get; } = clock;

        public static TimedApi Create(ApiFixture api)
        {
            var clock = new TestClock();
            var factory = api.CreateFactory(builder => builder.ConfigureTestServices(services =>
                services.AddSingleton<TimeProvider>(clock)));
            return new TimedApi(api, factory, clock);
        }

        public async Task<Student> StudentAsync()
        {
            var client = ApiFixture.CreateClient(factory, api.CreateToken(ApiFixture.NewClerkUserId()));
            var response = await client.PostAsJsonAsync("/v1/me", new { birthDate = "2000-05-17", termsAccepted = true });
            response.EnsureSuccessStatusCode();
            return new Student((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid(), client);
        }

        /// <summary>Grava a assinatura direto no banco; a ligação com o Mercado Pago vem na etapa 4c.</summary>
        public async Task<Subscription> SubscribeAsync(Student student, DateTimeOffset? nextBillingAt = null, string? cardLast4 = null)
        {
            var subscription = Subscription.Start(student.Id, $"pre-{Guid.NewGuid():N}", nextBillingAt, cardLast4, Clock.GetUtcNow());
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Subscriptions.Add(subscription);
            await db.SaveChangesAsync();
            return subscription;
        }

        public async Task<Guid> GrantAsync(Student student, CreditSource source, int amount, DateTimeOffset? expiresAt)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ICreditService>()
                .GrantAsync(new GrantCreditsCommand(student.Id, source, amount, expiresAt, "test"), CancellationToken.None);
            return await api.QueryAsync(db => db.CreditBuckets
                .Where(b => b.UserId == student.Id && b.Source == source)
                .OrderByDescending(b => b.CreatedAt).ThenByDescending(b => b.Id)
                .Select(b => b.Id).FirstAsync());
        }

        /// <summary>Uma cobrança do plano aprovada, como o job de pagamento fará na etapa 4c.</summary>
        public async Task<Guid> RenewAsync(Student student, DateTimeOffset cycleEnd)
        {
            var payment = Payment.StartPlan(student.Id, Guid.NewGuid().ToString(), null, Clock.GetUtcNow());
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Payments.Add(payment);
            await db.SaveChangesAsync();

            var unitOfWork = scope.ServiceProvider.GetRequiredService<RedigiAI.Application.Abstractions.IUnitOfWork>();
            var planCredits = scope.ServiceProvider.GetRequiredService<PlanCredits>();
            await unitOfWork.InTransactionAsync(
                async () =>
                {
                    await planCredits.RenewAsync(payment, cycleEnd, Clock.GetUtcNow(), CancellationToken.None);
                    return true;
                },
                CancellationToken.None);
            return await api.QueryAsync(db => db.CreditBuckets.Where(b => b.GrantKey == CreditBucket.PaymentGrantKey(payment.Id)).Select(b => b.Id).SingleAsync());
        }

        public async Task<bool> TryCreditBackAsync(Guid bucketId, DateTimeOffset now)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ICreditRepository>()
                .TryCreditBackAsync(bucketId, now, CancellationToken.None);
        }

        public async Task ExpireAsync()
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ICreditService>().ExpirePlanCreditsAsync(CancellationToken.None);
        }

        public Task<CreditBucket> BucketAsync(Guid bucketId) =>
            api.QueryAsync(db => db.CreditBuckets.AsNoTracking().SingleAsync(b => b.Id == bucketId));

        public ValueTask DisposeAsync() => factory.DisposeAsync();
    }
}
