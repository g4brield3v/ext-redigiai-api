using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using RedigiAI.Domain.Billing;
using RedigiAI.Domain.Common;

namespace RedigiAI.IntegrationTests;

[Collection(ApiCollection.Name)]
public class AccountTests(ApiFixture api)
{
    private static DateOnly Today => BrasiliaTime.DateOf(DateTimeOffset.UtcNow);

    private static object Registration(DateOnly? birthDate, bool termsAccepted = true) =>
        new { birthDate = birthDate?.ToString("yyyy-MM-dd"), termsAccepted };

    private static object Adult => Registration(new DateOnly(2000, 5, 17));

    [Fact]
    public async Task Request_without_token_is_refused_in_the_error_format()
    {
        var response = await api.CreateClient().GetAsync("/v1/me");

        var error = await AssertErrorAsync(response, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        Assert.Equal("Entre na sua conta para continuar.", error.GetProperty("message").GetString());
        Assert.False(error.GetProperty("retryable").GetBoolean());
        Assert.False(string.IsNullOrEmpty(error.GetProperty("traceId").GetString()));
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("wrong-issuer")]
    [InlineData("wrong-key")]
    [InlineData("wrong-origin")]
    [InlineData("no-origin")]
    [InlineData("garbage")]
    public async Task Invalid_token_is_refused(string kind)
    {
        var userId = ApiFixture.NewClerkUserId();
        var token = kind switch
        {
            "expired" => api.CreateToken(userId, expires: DateTime.UtcNow.AddMinutes(-5)),
            "wrong-issuer" => api.CreateToken(userId, issuer: "https://clerk.evil.test"),
            "wrong-key" => api.CreateToken(userId, signingKey: new RsaSecurityKey(RSA.Create(2048)) { KeyId = "test-key" }),
            "wrong-origin" => api.CreateToken(userId, authorizedParty: "https://evil.test"),
            "no-origin" => api.CreateToken(userId, authorizedParty: null),
            _ => "not-a-jwt",
        };

        var response = await api.CreateClient(token).PostAsJsonAsync("/v1/me", Adult);

        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        Assert.False(await api.QueryAsync(db => db.Users.AnyAsync(u => u.ClerkUserId == userId)));
    }

    [Fact]
    public async Task First_access_creates_the_user_with_one_free_credit()
    {
        var userId = ApiFixture.NewClerkUserId();
        var client = api.CreateClient(api.CreateToken(userId, email: "ana@example.com", name: "Ana Souza"));

        var response = await client.PostAsJsonAsync("/v1/me", Adult);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var profile = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ana@example.com", profile.GetProperty("email").GetString());
        Assert.Equal("Ana Souza", profile.GetProperty("name").GetString());
        Assert.False(profile.GetProperty("termsAcceptanceRequired").GetBoolean());
        Assert.False(profile.GetProperty("aiNoticeAccepted").GetBoolean());
        Assert.Equal(1, profile.GetProperty("credits").GetProperty("total").GetInt32());
        var bucket = Assert.Single(profile.GetProperty("credits").GetProperty("buckets").EnumerateArray());
        Assert.Equal("trial", bucket.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, bucket.GetProperty("expiresAt").ValueKind);
        Assert.False(profile.GetProperty("emailPreferences").GetProperty("newThemes").GetBoolean());

        var user = await api.QueryAsync(db => db.Users.SingleAsync(u => u.ClerkUserId == userId));
        Assert.Equal(new DateOnly(2000, 5, 17), user.BirthDate.Value);
        Assert.Equal("1", user.TermsVersion);
        Assert.Equal(7, user.Id.Version);
        Assert.Equal(
            ["credit.granted", "user.registered"],
            await api.QueryAsync(db => db.AuditLog
                .Where(a => a.EntityId == user.Id || a.Actor == "system" && db.CreditBuckets.Any(b => b.Id == a.EntityId && b.UserId == user.Id))
                .Select(a => a.Action).OrderBy(a => a).ToListAsync()));
    }

    [Fact]
    public async Task Someone_who_turns_16_today_is_accepted()
    {
        var client = api.CreateClient(api.CreateToken(ApiFixture.NewClerkUserId()));

        var response = await client.PostAsJsonAsync("/v1/me", Registration(Today.AddYears(-16)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Under_16_is_refused_and_nothing_is_created()
    {
        var userId = ApiFixture.NewClerkUserId();
        var client = api.CreateClient(api.CreateToken(userId));

        var response = await client.PostAsJsonAsync("/v1/me", Registration(Today.AddYears(-16).AddDays(1)));

        var error = await AssertErrorAsync(response, HttpStatusCode.UnprocessableEntity, "AGE_BELOW_MINIMUM");
        Assert.Equal("A plataforma é destinada a pessoas com 16 anos ou mais.", error.GetProperty("message").GetString());
        Assert.False(await api.QueryAsync(db => db.Users.AnyAsync(u => u.ClerkUserId == userId)));
        await AssertErrorAsync(await client.GetAsync("/v1/me"), HttpStatusCode.NotFound, "PROFILE_NOT_FOUND");
    }

    [Fact]
    public async Task Birth_date_and_terms_are_required()
    {
        var userId = ApiFixture.NewClerkUserId();
        var client = api.CreateClient(api.CreateToken(userId));

        await AssertErrorAsync(
            await client.PostAsJsonAsync("/v1/me", Registration(null)), HttpStatusCode.UnprocessableEntity, "VALIDATION_ERROR");
        await AssertErrorAsync(
            await client.PostAsJsonAsync("/v1/me", Registration(new DateOnly(2000, 5, 17), termsAccepted: false)),
            HttpStatusCode.UnprocessableEntity,
            "VALIDATION_ERROR");
        await AssertErrorAsync(
            await client.PostAsync("/v1/me", new StringContent("{not json", System.Text.Encoding.UTF8, "application/json")),
            HttpStatusCode.UnprocessableEntity,
            "VALIDATION_ERROR");

        Assert.False(await api.QueryAsync(db => db.Users.AnyAsync(u => u.ClerkUserId == userId)));
    }

    [Fact]
    public async Task Repeating_the_first_access_does_not_grant_a_second_credit()
    {
        var userId = ApiFixture.NewClerkUserId();
        var client = api.CreateClient(api.CreateToken(userId));

        var first = await client.PostAsJsonAsync("/v1/me", Adult);
        var second = await client.PostAsJsonAsync("/v1/me", Adult);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(1, (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("credits").GetProperty("total").GetInt32());
        await AssertSingleTrialCreditAsync(userId);
    }

    [Fact]
    public async Task New_terms_block_submissions_until_the_student_accepts_them()
    {
        var userId = ApiFixture.NewClerkUserId();
        var client = api.CreateClient(api.CreateToken(userId));
        (await client.PostAsJsonAsync("/v1/me", Adult)).EnsureSuccessStatusCode();
        // A versão vigente muda depois do aceite.
        await using var factory = api.CreateFactory(builder => builder.UseSetting("Legal:TermsVersion", "2"));
        var updated = ApiFixture.CreateClient(factory, api.CreateToken(userId));

        Assert.True((await updated.GetFromJsonAsync<JsonElement>("/v1/me")).GetProperty("termsAcceptanceRequired").GetBoolean());
        var blocked = await updated.PostAsJsonAsync("/v1/essays/batch", new { items = Array.Empty<object>() });
        await AssertErrorAsync(blocked, HttpStatusCode.Conflict, "TERMS_ACCEPTANCE_REQUIRED");

        var accepted = await updated.PostAsync("/v1/me/terms", null);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.False((await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("termsAcceptanceRequired").GetBoolean());
        Assert.NotEqual(HttpStatusCode.Conflict, (await updated.PostAsJsonAsync("/v1/essays/batch", new { items = Array.Empty<object>() })).StatusCode);
    }

    [Fact]
    public void Every_route_declares_whether_it_needs_login()
    {
        // A política padrão já fecha o que não declara nada; o teste mantém a intenção explícita em cada controller.
        var endpoints = api.Factory.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor>() is not null)
            .ToList();

        Assert.NotEmpty(endpoints);
        Assert.All(endpoints, e => Assert.True(
            e.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is not null
                || e.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>().Any(),
            e.RoutePattern.RawText));
    }

    [Fact]
    public void Only_health_and_signed_webhooks_open_without_login()
    {
        // Nada do conteúdo da plataforma abre sem login: só a saúde da API e os avisos assinados do Mercado Pago e do Clerk.
        // Todas as rotas, inclusive as declaradas fora dos controllers; a de fallback responde 404 e fica de fora.
        var anonymous = api.Factory.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is not null
                && e.RoutePattern.RawText != "{*path:nonfile}" && !e.RoutePattern.RawText!.Contains("{**"))
            .Select(e => e.RoutePattern.RawText!.TrimStart('/'))
            .Order()
            .ToList();

        Assert.Equal(["health", "ready", "v1/webhooks/clerk", "v1/webhooks/mercadopago"], anonymous);
    }

    [Fact]
    public async Task A_clerk_session_without_a_platform_account_does_not_read_the_catalog()
    {
        // Conta no Clerk sem o cadastro na plataforma (nunca concluiu, ou foi recusada pela idade).
        var client = api.CreateClient(api.CreateToken(ApiFixture.NewClerkUserId()));

        await AssertErrorAsync(await client.GetAsync("/v1/exams"), HttpStatusCode.NotFound, "PROFILE_NOT_FOUND");
        await AssertErrorAsync(await client.GetAsync("/v1/prompts/algoritmos-e-o-debate-publico"), HttpStatusCode.NotFound, "PROFILE_NOT_FOUND");
    }

    [Theory]
    [InlineData("/v1/me")]
    [InlineData("/v1/essays")]
    [InlineData("/v1/payments")]
    [InlineData("/v1/admin/prompts")]
    [InlineData("/v1/exams")]
    [InlineData("/v1/exams/fuvest/prompts")]
    [InlineData("/v1/prompts/algoritmos-e-o-debate-publico")]
    [InlineData("/v1/rubrics/0198f0c0-0000-7000-8000-000000000000")]
    public async Task Logged_routes_without_a_token_answer_401(string path)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Simultaneous_first_accesses_grant_exactly_one_credit()
    {
        var userId = ApiFixture.NewClerkUserId();
        var token = api.CreateToken(userId);

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            api.CreateClient(token).PostAsJsonAsync("/v1/me", Adult)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(19, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        foreach (var response in responses)
        {
            var profile = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(1, profile.GetProperty("credits").GetProperty("total").GetInt32());
        }

        await AssertSingleTrialCreditAsync(userId);
    }

    [Fact]
    public async Task One_student_never_reads_or_changes_another_students_profile()
    {
        var (anaId, brunoId) = (ApiFixture.NewClerkUserId(), ApiFixture.NewClerkUserId());
        var ana = api.CreateClient(api.CreateToken(anaId, email: "ana@example.com", name: "Ana"));
        var bruno = api.CreateClient(api.CreateToken(brunoId, email: "bruno@example.com", name: "Bruno"));
        await ana.PostAsJsonAsync("/v1/me", Adult);
        await bruno.PostAsJsonAsync("/v1/me", Adult);

        var patch = await ana.PatchAsJsonAsync("/v1/me", new { name = "Ana Maria", emailPreferences = new { newThemes = true } });

        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        var anaProfile = await ana.GetFromJsonAsync<JsonElement>("/v1/me");
        var brunoProfile = await bruno.GetFromJsonAsync<JsonElement>("/v1/me");
        Assert.Equal("Ana Maria", anaProfile.GetProperty("name").GetString());
        Assert.True(anaProfile.GetProperty("emailPreferences").GetProperty("newThemes").GetBoolean());
        Assert.True(anaProfile.GetProperty("emailPreferences").GetProperty("correctionReady").GetBoolean());
        Assert.Equal("Bruno", brunoProfile.GetProperty("name").GetString());
        Assert.Equal("bruno@example.com", brunoProfile.GetProperty("email").GetString());
        Assert.False(brunoProfile.GetProperty("emailPreferences").GetProperty("newThemes").GetBoolean());
        Assert.NotEqual(anaProfile.GetProperty("id").GetGuid(), brunoProfile.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Profile_update_validates_the_name()
    {
        var client = api.CreateClient(api.CreateToken(ApiFixture.NewClerkUserId()));
        await client.PostAsJsonAsync("/v1/me", Adult);

        var tooLong = await client.PatchAsJsonAsync("/v1/me", new { name = new string('a', 121) });
        var controlCharacter = await client.PatchAsJsonAsync("/v1/me", new { name = "Ana\u0000Souza" });

        await AssertErrorAsync(tooLong, HttpStatusCode.UnprocessableEntity, "VALIDATION_ERROR");
        await AssertErrorAsync(controlCharacter, HttpStatusCode.UnprocessableEntity, "VALIDATION_ERROR");
    }

    [Fact]
    public async Task Access_is_logged_once_per_login_session()
    {
        var userId = ApiFixture.NewClerkUserId();
        var firstSession = api.CreateClient(api.CreateToken(userId, sessionId: "sess_first"));
        firstSession.DefaultRequestHeaders.UserAgent.ParseAdd("RedigiAI-Tests/1.0");
        await firstSession.PostAsJsonAsync("/v1/me", Adult);
        await firstSession.GetAsync("/v1/me");
        await firstSession.GetAsync("/v1/me");

        var secondSession = api.CreateClient(api.CreateToken(userId, sessionId: "sess_second"));
        await secondSession.GetAsync("/v1/me");
        await secondSession.GetAsync("/v1/me");

        var logs = await api.QueryAsync(db => db.AccessLogs
            .Where(l => db.Users.Any(u => u.Id == l.UserId && u.ClerkUserId == userId))
            .OrderBy(l => l.OccurredAt)
            .ToListAsync());
        Assert.Equal(["sess_first", "sess_second"], logs.Select(l => l.SessionId));
        Assert.Equal("RedigiAI-Tests/1.0", logs[0].UserAgent);
        Assert.All(logs, l => Assert.False(string.IsNullOrEmpty(l.Ip)));
    }

    private async Task AssertSingleTrialCreditAsync(string clerkUserId)
    {
        var user = await api.QueryAsync(db => db.Users.SingleAsync(u => u.ClerkUserId == clerkUserId));
        var bucket = await api.QueryAsync(db => db.CreditBuckets.SingleAsync(b => b.UserId == user.Id));
        var entry = await api.QueryAsync(db => db.CreditLedger.SingleAsync(e => e.UserId == user.Id));

        Assert.Equal((CreditSource.Trial, 1, 1), (bucket.Source, bucket.Granted, bucket.Remaining));
        Assert.Null(bucket.ExpiresAt);
        Assert.Equal((CreditSource.Trial, 1, bucket.Id), (entry.Source, entry.Delta, entry.BucketId));
    }

    internal static async Task<JsonElement> AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.Equal(
            ["code", "message", "retryable", "traceId"],
            error.EnumerateObject().Select(p => p.Name).Order());
        return error;
    }
}
