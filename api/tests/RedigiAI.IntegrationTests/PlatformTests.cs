using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;

namespace RedigiAI.IntegrationTests;

[Collection(ApiCollection.Name)]
public class PlatformTests(ApiFixture api)
{
    private static readonly object Adult = new { birthDate = "2000-05-17", termsAccepted = true };

    [Fact]
    public async Task Health_and_ready_respond()
    {
        var client = api.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/ready")).StatusCode);
    }

    [Fact]
    public async Task Responses_carry_security_headers_and_trace_id()
    {
        var response = await api.CreateClient().GetAsync("/v1/exams");

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.NotEmpty(response.Headers.GetValues("X-Trace-Id").Single());
    }

    [Theory]
    [InlineData(ApiFixture.WebOrigin, true)]
    [InlineData("https://evil.test", false)]
    public async Task Cors_allows_only_the_configured_origin(string origin, bool allowed)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/exams");
        request.Headers.Add("Origin", origin);

        var response = await api.CreateClient().SendAsync(request);

        Assert.Equal(allowed, response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Requests_above_the_limit_are_refused_with_rate_limited()
    {
        await using var factory = api.CreateFactory(builder => builder.UseSetting("RateLimit:PerIpPerMinute", "3"));
        var client = ApiFixture.CreateClient(factory);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            statuses.Add((await client.GetAsync("/v1/exams")).StatusCode);
        }

        // Sem login, o catálogo responde 401, e o limite por IP vale do mesmo jeito.
        Assert.Equal(Enumerable.Repeat(HttpStatusCode.Unauthorized, 3), statuses.Take(3));
        var refused = await client.GetAsync("/v1/exams");
        var error = await AccountTests.AssertErrorAsync(refused, HttpStatusCode.TooManyRequests, "RATE_LIMITED");
        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task Clerk_webhook_with_valid_signature_updates_the_email()
    {
        var userId = ApiFixture.NewClerkUserId();
        await api.CreateClient(api.CreateToken(userId, email: "antigo@example.com")).PostAsJsonAsync("/v1/me", Adult);

        var response = await api.CreateClient().SendAsync(api.CreateClerkWebhook(UserUpdated(userId, "novo@example.com")));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("novo@example.com", await EmailOfAsync(userId));
    }

    [Fact]
    public async Task Clerk_webhook_with_invalid_signature_is_refused_before_anything_is_written()
    {
        var userId = ApiFixture.NewClerkUserId();
        await api.CreateClient(api.CreateToken(userId, email: "antigo@example.com")).PostAsJsonAsync("/v1/me", Adult);
        var body = UserUpdated(userId, "invasor@example.com");
        var client = api.CreateClient();

        var wrongKey = await client.SendAsync(api.CreateClerkWebhook(body, key: RandomNumberGenerator.GetBytes(24)));
        var tampered = await client.SendAsync(api.CreateClerkWebhook(body, signedJson: UserUpdated(userId, "antigo@example.com")));
        var unsigned = await client.PostAsync("/v1/webhooks/clerk", new StringContent(body));

        await AccountTests.AssertErrorAsync(wrongKey, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        await AccountTests.AssertErrorAsync(tampered, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        await AccountTests.AssertErrorAsync(unsigned, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        Assert.Equal("antigo@example.com", await EmailOfAsync(userId));
    }

    private Task<string> EmailOfAsync(string clerkUserId) =>
        api.QueryAsync(db => db.Users.Where(u => u.ClerkUserId == clerkUserId).Select(u => u.Email).SingleAsync());

    private static string UserUpdated(string clerkUserId, string email) =>
        $$"""
        {
          "type": "user.updated",
          "data": {
            "id": "{{clerkUserId}}",
            "primary_email_address_id": "idn_2",
            "email_addresses": [
              { "id": "idn_1", "email_address": "outro@example.com" },
              { "id": "idn_2", "email_address": "{{email}}" }
            ]
          }
        }
        """;
}
