using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace RedigiAI.IntegrationTests;

[Collection(ApiCollection.Name)]
public class DevAccessTests(ApiFixture api)
{
    private const string Header = "X-Dev-User";

    [Theory]
    [InlineData("ana")]
    [InlineData("admin")]
    public async Task Dev_header_is_ignored_when_dev_access_is_off(string user)
    {
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Add(Header, user);

        await AccountTests.AssertErrorAsync(await client.GetAsync("/v1/me"), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        await AccountTests.AssertErrorAsync(await client.GetAsync("/v1/admin/prompts"), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Theory]
    [InlineData("Testing")]
    [InlineData("Production")]
    public async Task Api_refuses_to_start_with_dev_access_outside_development(string environment)
    {
        await using var factory = api.CreateFactory(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("DevAuth:Enabled", "true");
        });

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("DevAuth__Enabled", error.ToString());
    }

    [Fact]
    public async Task Test_students_enter_without_login_in_development()
    {
        await using var factory = DevFactory();

        var ana = await DevClient(factory, "ana").GetFromJsonAsync<JsonElement>("/v1/me");
        Assert.Equal("ana.souza@example.com", ana.GetProperty("email").GetString());
        Assert.Equal(6, ana.GetProperty("credits").GetProperty("total").GetInt32());

        var bruno = await DevClient(factory, "bruno").GetFromJsonAsync<JsonElement>("/v1/me");
        Assert.Equal(1, bruno.GetProperty("credits").GetProperty("total").GetInt32());

        await AccountTests.AssertErrorAsync(
            await DevClient(factory, "nova").GetAsync("/v1/me"), HttpStatusCode.NotFound, "PROFILE_NOT_FOUND");
        await AccountTests.AssertErrorAsync(
            await DevClient(factory, "ninguem").GetAsync("/v1/me"), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Fact]
    public async Task Test_admin_enters_in_development_with_the_second_factor_simulated()
    {
        await using var factory = DevFactory();

        var admin = await DevClient(factory, "admin").GetFromJsonAsync<JsonElement>("/v1/me");

        Assert.Equal(ApiFixture.AdminEmail, admin.GetProperty("email").GetString());
        Assert.Equal("admin", admin.GetProperty("role").GetString());
        // Os alunos de teste continuam alunos.
        var ana = await DevClient(factory, "ana").GetFromJsonAsync<JsonElement>("/v1/me");
        Assert.Equal("student", ana.GetProperty("role").GetString());

        // A exigência do segundo fator está ligada nos testes: o administrador de teste passa, a aluna não entra.
        Assert.Equal(HttpStatusCode.OK, (await DevClient(factory, "admin").GetAsync("/v1/admin/prompts")).StatusCode);
        await AccountTests.AssertErrorAsync(
            await DevClient(factory, "ana").GetAsync("/v1/admin/prompts"), HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Test_admin_does_not_exist_without_a_configured_admin_email()
    {
        await using var factory = api.CreateFactory(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("DevAuth:Enabled", "true");
            builder.UseSetting("Admin:Emails", "");
        });

        await AccountTests.AssertErrorAsync(
            await DevClient(factory, "admin").GetAsync("/v1/me"), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Fact]
    public async Task Starting_again_does_not_grant_credits_twice()
    {
        await using (var first = DevFactory())
        {
            await DevClient(first, "ana").GetAsync("/v1/me");
        }

        await using var second = DevFactory();
        var ana = await DevClient(second, "ana").GetFromJsonAsync<JsonElement>("/v1/me");

        Assert.Equal(6, ana.GetProperty("credits").GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Tokens_still_work_next_to_dev_access()
    {
        await using var factory = DevFactory();
        var client = ApiFixture.CreateClient(factory, api.CreateToken(ApiFixture.NewClerkUserId()));

        await AccountTests.AssertErrorAsync(await client.GetAsync("/v1/me"), HttpStatusCode.NotFound, "PROFILE_NOT_FOUND");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cors_accepts_the_dev_header_only_with_dev_access(bool devAccess)
    {
        await using var factory = devAccess ? DevFactory() : api.CreateFactory();
        var preflight = new HttpRequestMessage(HttpMethod.Options, "/v1/me");
        preflight.Headers.Add("Origin", ApiFixture.WebOrigin);
        preflight.Headers.Add("Access-Control-Request-Method", "GET");
        preflight.Headers.Add("Access-Control-Request-Headers", "x-dev-user");

        var response = await factory.CreateClient().SendAsync(preflight);

        var allowed = response.Headers.TryGetValues("Access-Control-Allow-Headers", out var values) ? string.Join(",", values) : "";
        Assert.Equal(devAccess, allowed.Contains("x-dev-user", StringComparison.OrdinalIgnoreCase));
    }

    private WebApplicationFactory<Program> DevFactory() => api.CreateFactory(builder =>
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("DevAuth:Enabled", "true");
    });

    private static HttpClient DevClient(WebApplicationFactory<Program> factory, string user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Header, user);
        return client;
    }
}
