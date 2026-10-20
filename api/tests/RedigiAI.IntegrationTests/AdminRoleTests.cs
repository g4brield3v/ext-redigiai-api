using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace RedigiAI.IntegrationTests;

/// <summary>
/// Administrador é quem tem a flag <c>is_admin</c> no banco, ligada à mão. Nem o e-mail, nem o navegador, nem o token
/// fazem alguém administrador; a flag é lida a cada requisição.
/// </summary>
[Collection(ApiCollection.Name)]
public class AdminRoleTests(ApiFixture api)
{
    [Fact]
    public async Task A_new_account_is_never_admin_whatever_the_email()
    {
        var (_, client) = await RegisterAsync(ApiFixture.AdminEmail);

        var profile = await client.GetFromJsonAsync<JsonElement>("/v1/me");

        Assert.False(profile.GetProperty("isAdmin").GetBoolean());
        await AccountTests.AssertErrorAsync(await client.GetAsync("/v1/admin/prompts"), HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task The_flag_in_the_database_opens_the_admin_area_and_removing_it_closes_it_on_the_next_request()
    {
        var (userId, client) = await RegisterAsync();

        await api.MakeAdminAsync(userId);
        Assert.True((await client.GetFromJsonAsync<JsonElement>("/v1/me")).GetProperty("isAdmin").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/admin/prompts")).StatusCode);

        // Sem esperar novo login: a próxima requisição já não entra.
        await api.MakeAdminAsync(userId, isAdmin: false);
        await AccountTests.AssertErrorAsync(await client.GetAsync("/v1/admin/prompts"), HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/v1/me")).GetProperty("isAdmin").GetBoolean());
    }

    [Fact]
    public async Task The_browser_cannot_turn_itself_into_admin()
    {
        var (userId, client) = await RegisterAsync();

        var patched = await client.PatchAsJsonAsync("/v1/me", new { name = "Ana", isAdmin = true, role = "admin" });
        var registeredAgain = await client.PostAsJsonAsync("/v1/me", new { birthDate = "2000-05-17", termsAccepted = true, isAdmin = true });

        Assert.True(patched.IsSuccessStatusCode);
        Assert.True(registeredAgain.IsSuccessStatusCode);
        Assert.False(await api.QueryAsync(db => db.Users.Where(u => u.ClerkUserId == userId).Select(u => u.IsAdmin).SingleAsync()));
        await AccountTests.AssertErrorAsync(await client.GetAsync("/v1/admin/prompts"), HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Admin_routes_without_login_answer_401()
    {
        var anonymous = api.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/v1/admin/prompts")).StatusCode);
    }

    private async Task<(string UserId, HttpClient Client)> RegisterAsync(string? email = null)
    {
        var userId = ApiFixture.NewClerkUserId();
        var client = api.CreateClient(api.CreateToken(userId, email: email));
        (await client.PostAsJsonAsync("/v1/me", new { birthDate = "2000-05-17", termsAccepted = true })).EnsureSuccessStatusCode();
        return (userId, client);
    }
}
