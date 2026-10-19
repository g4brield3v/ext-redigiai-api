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
/// Calendário do plano semanal (etapa 4): 3 correções toda segunda-feira 00h e o vencimento no dia 1º 00h, no
/// horário de Brasília, com o relógio do teste.
/// </summary>
[Collection(ApiCollection.Name)]
public class CreditCalendarTests(ApiFixture api)
{
    // Brasília é UTC−3 o ano todo. As datas ficam longe de hoje para não esbarrar nos créditos dos outros testes.
    private static DateTimeOffset Brasilia(int year, int month, int day, int hour = 0, int minute = 0) =>
        new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.FromHours(-3)).ToUniversalTime();

    [Fact]
    public async Task Monday_grants_three_credits_once_to_each_active_subscription_until_the_next_first()
    {
        await using var calendar = Calendar.Create(api);
        calendar.Clock.Now = Brasilia(2031, 3, 5, 10); // quarta-feira
        var student = await calendar.StudentAsync();
        var subscription = await calendar.SubscribeAsync(student, nextBillingAt: Brasilia(2031, 4, 5));

        calendar.Clock.Now = Brasilia(2031, 3, 10, 0, 1); // segunda 00h01
        await calendar.GrantWeeklyAsync();
        await calendar.GrantWeeklyAsync();

        var bucket = Assert.Single(await calendar.PlanBucketsAsync(student));
        Assert.Equal(3, bucket.Granted);
        Assert.Equal(3, bucket.Remaining);
        Assert.Equal(Brasilia(2031, 4, 1), bucket.ExpiresAt);
        Assert.Equal($"plan_week:{subscription.Id}:2031-03-10", bucket.GrantKey);
        var entry = Assert.Single(await api.QueryAsync(db => db.CreditLedger.Where(e => e.BucketId == bucket.Id).ToListAsync()));
        Assert.Equal(3, entry.Delta);
        Assert.Equal(CreditSource.PlanWeek, entry.Source);
        Assert.Single(await api.QueryAsync(db => db.AuditLog
            .Where(a => a.Action == "credit.granted" && a.EntityId == bucket.Id).ToListAsync()));

        // Na semana seguinte, mais 3, com o mesmo vencimento.
        calendar.Clock.Now = Brasilia(2031, 3, 17, 0, 5);
        await calendar.GrantWeeklyAsync();
        Assert.Equal(
            [Brasilia(2031, 4, 1), Brasilia(2031, 4, 1)],
            (await calendar.PlanBucketsAsync(student)).Select(b => b.ExpiresAt).ToList());
    }

    [Fact]
    public async Task Paused_subscriptions_and_those_started_after_the_monday_get_nothing_that_week()
    {
        await using var calendar = Calendar.Create(api);
        calendar.Clock.Now = Brasilia(2031, 5, 7, 9);
        var paused = await calendar.StudentAsync();
        await calendar.SubscribeAsync(paused, status: SubscriptionStatus.Paused);
        var cancelled = await calendar.StudentAsync();
        await calendar.SubscribeAsync(cancelled, status: SubscriptionStatus.Cancelled);

        // Assina na própria segunda, depois das 00h: a concessão dessa semana é a imediata do pagamento (etapa 4c).
        calendar.Clock.Now = Brasilia(2031, 5, 12, 8);
        var late = await calendar.StudentAsync();
        await calendar.SubscribeAsync(late);
        await calendar.GrantWeeklyAsync();

        Assert.Empty(await calendar.PlanBucketsAsync(paused));
        Assert.Empty(await calendar.PlanBucketsAsync(cancelled));
        Assert.Empty(await calendar.PlanBucketsAsync(late));
    }

    [Fact]
    public async Task Simultaneous_sweeps_grant_the_week_once()
    {
        await using var calendar = Calendar.Create(api);
        calendar.Clock.Now = Brasilia(2031, 6, 4, 12);
        var student = await calendar.StudentAsync();
        await calendar.SubscribeAsync(student);

        calendar.Clock.Now = Brasilia(2031, 6, 9, 0, 2);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => calendar.GrantWeeklyAsync()));

        var bucket = Assert.Single(await calendar.PlanBucketsAsync(student));
        Assert.Single(await api.QueryAsync(db => db.CreditLedger.Where(e => e.BucketId == bucket.Id).ToListAsync()));
    }

    [Fact]
    public async Task When_the_month_changes_mid_week_the_monday_credits_expire_on_the_first()
    {
        await using var calendar = Calendar.Create(api);
        calendar.Clock.Now = Brasilia(2031, 4, 22, 15);
        var student = await calendar.StudentAsync();
        await calendar.SubscribeAsync(student);

        // Segunda 28/04: as correções vencem na quinta, dia 1º de maio.
        calendar.Clock.Now = Brasilia(2031, 4, 28, 0, 3);
        await calendar.GrantWeeklyAsync();

        Assert.Equal(Brasilia(2031, 5, 1), Assert.Single(await calendar.PlanBucketsAsync(student)).ExpiresAt);
    }

    [Fact]
    public async Task A_sweep_that_only_runs_after_the_first_does_not_grant_the_week_that_already_expired()
    {
        await using var calendar = Calendar.Create(api);
        calendar.Clock.Now = Brasilia(2032, 3, 24, 15);
        var student = await calendar.StudentAsync();
        await calendar.SubscribeAsync(student);

        // Segunda 29/03/2032, com o dia 1º na quinta: o worker parado só volta na sexta, 02/04.
        calendar.Clock.Now = Brasilia(2032, 4, 2, 9);
        await calendar.GrantWeeklyAsync();

        Assert.Empty(await calendar.PlanBucketsAsync(student));
    }

    [Fact]
    public async Task On_the_first_the_unused_plan_credits_expire_once_with_a_negative_entry()
    {
        await using var calendar = Calendar.Create(api);
        calendar.Clock.Now = Brasilia(2031, 7, 21, 10);
        var student = await calendar.StudentAsync();
        var plan = await calendar.GrantAsync(student, CreditSource.PlanWeek, 3, Brasilia(2031, 8, 1));
        var later = await calendar.GrantAsync(student, CreditSource.PlanWeek, 3, Brasilia(2031, 9, 1));
        var single = await calendar.GrantAsync(student, CreditSource.Single, 2, expiresAt: null);

        // Um minuto antes da meia-noite do dia 1º nada vence.
        calendar.Clock.Now = Brasilia(2031, 7, 31, 23, 59);
        await calendar.ExpireAsync();
        Assert.Equal(3, (await calendar.BucketAsync(plan)).Remaining);

        calendar.Clock.Now = Brasilia(2031, 8, 1, 0, 4);
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => calendar.ExpireAsync()));
        await calendar.ExpireAsync();

        Assert.Equal(0, (await calendar.BucketAsync(plan)).Remaining);
        var expiry = Assert.Single(await api.QueryAsync(db => db.CreditLedger
            .Where(e => e.BucketId == plan && e.Delta < 0 && e.EssayId == null).ToListAsync()));
        Assert.Equal(-3, expiry.Delta);
        Assert.Equal(CreditSource.PlanWeek, expiry.Source);
        Assert.Equal(1, await api.QueryAsync(db => db.AuditLog.CountAsync(a => a.Action == "credit.expired" && a.EntityId == plan)));

        // O que não venceu fica como estava.
        Assert.Equal(3, (await calendar.BucketAsync(later)).Remaining);
        Assert.Equal(2, (await calendar.BucketAsync(single)).Remaining);

        // O ledger continua fechando com o saldo de cada bucket.
        foreach (var bucketId in new[] { plan, later, single })
        {
            var sum = await api.QueryAsync(db => db.CreditLedger.Where(e => e.BucketId == bucketId).SumAsync(e => e.Delta));
            Assert.Equal((await calendar.BucketAsync(bucketId)).Remaining, sum);
        }
    }

    [Fact]
    public async Task The_profile_shows_the_subscription_and_the_plan_credits_with_their_expiry()
    {
        await using var calendar = Calendar.Create(api);
        calendar.Clock.Now = Brasilia(2031, 10, 1, 9);
        var student = await calendar.StudentAsync();
        var newcomer = await calendar.StudentAsync();
        await calendar.SubscribeAsync(student, nextBillingAt: Brasilia(2031, 11, 1, 9), cardLast4: "4242");
        calendar.Clock.Now = Brasilia(2031, 10, 6, 0, 1);
        await calendar.GrantWeeklyAsync();

        var profile = await student.Client.GetFromJsonAsync<JsonElement>("/v1/me");

        var subscription = profile.GetProperty("subscription");
        Assert.Equal("active", subscription.GetProperty("status").GetString());
        Assert.Equal(Brasilia(2031, 11, 1, 9), subscription.GetProperty("nextBillingAt").GetDateTimeOffset());
        Assert.Equal("4242", subscription.GetProperty("cardLast4").GetString());
        var credits = profile.GetProperty("credits");
        Assert.Equal(4, credits.GetProperty("total").GetInt32()); // 3 do plano + o teste grátis
        var planBucket = credits.GetProperty("buckets").EnumerateArray().Single(b => b.GetProperty("source").GetString() == "plan_week");
        Assert.Equal(Brasilia(2031, 11, 1), planBucket.GetProperty("expiresAt").GetDateTimeOffset());

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

    /// <summary>A API com o relógio do teste, mais atalhos para assinar, conceder e varrer o calendário.</summary>
    private sealed class Calendar(ApiFixture api, WebApplicationFactory<Program> factory, TestClock clock) : IAsyncDisposable
    {
        public TestClock Clock { get; } = clock;

        public static Calendar Create(ApiFixture api)
        {
            var clock = new TestClock();
            var factory = api.CreateFactory(builder => builder.ConfigureTestServices(services =>
                services.AddSingleton<TimeProvider>(clock)));
            return new Calendar(api, factory, clock);
        }

        public async Task<Student> StudentAsync()
        {
            var client = ApiFixture.CreateClient(factory, api.CreateToken(ApiFixture.NewClerkUserId()));
            var response = await client.PostAsJsonAsync("/v1/me", new { birthDate = "2000-05-17", termsAccepted = true });
            response.EnsureSuccessStatusCode();
            return new Student((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid(), client);
        }

        /// <summary>Grava a assinatura direto no banco; a ligação com o Mercado Pago vem na etapa 4c.</summary>
        public async Task<Subscription> SubscribeAsync(
            Student student,
            SubscriptionStatus status = SubscriptionStatus.Active,
            DateTimeOffset? nextBillingAt = null,
            string? cardLast4 = null)
        {
            var subscription = Subscription.Start(student.Id, $"pre-{Guid.NewGuid():N}", nextBillingAt, cardLast4, Clock.GetUtcNow());
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Subscriptions.Add(subscription);
            await db.SaveChangesAsync();
            if (status != SubscriptionStatus.Active)
            {
                await db.Subscriptions.Where(s => s.Id == subscription.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, status));
            }

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

        public async Task GrantWeeklyAsync()
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ICreditService>().GrantWeeklyPlanCreditsAsync(CancellationToken.None);
        }

        public async Task ExpireAsync()
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ICreditService>().ExpirePlanCreditsAsync(CancellationToken.None);
        }

        public Task<List<CreditBucket>> PlanBucketsAsync(Student student) =>
            api.QueryAsync(db => db.CreditBuckets
                .Where(b => b.UserId == student.Id && b.Source == CreditSource.PlanWeek)
                .OrderBy(b => b.CreatedAt).ToListAsync());

        public Task<CreditBucket> BucketAsync(Guid bucketId) =>
            api.QueryAsync(db => db.CreditBuckets.AsNoTracking().SingleAsync(b => b.Id == bucketId));

        public ValueTask DisposeAsync() => factory.DisposeAsync();
    }
}
