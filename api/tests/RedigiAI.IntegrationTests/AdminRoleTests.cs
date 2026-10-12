using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RedigiAI.Application.Abstractions;
using RedigiAI.Application.Accounts;
using RedigiAI.Domain.Accounts;
using RedigiAI.Domain.Common;

namespace RedigiAI.IntegrationTests;

/// <summary>Quem é administrador (docs/07-propostas-de-redacao.md, A5).</summary>
[Collection(ApiCollection.Name)]
public class AdminRoleTests(ApiFixture api)
{
    private static readonly object Adult = new { birthDate = "2000-05-17", termsAccepted = true };

    [Fact]
    public async Task Listed_and_verified_email_becomes_admin_on_first_access()
    {
        var userId = Account(ApiFixture.AdminEmail, verified: true);

        var profile = await RegisterAsync(userId, ApiFixture.AdminEmail);

        Assert.Equal("admin", profile.GetProperty("role").GetString());
        Assert.Equal(UserRole.Admin, await RoleOfAsync(userId));
        Assert.Contains("user.role_changed", await AuditActionsAsync(userId));
    }

    [Fact]
    public async Task Listed_email_that_is_not_verified_stays_student()
    {
        var userId = Account(ApiFixture.AdminEmail, verified: false);

        var profile = await RegisterAsync(userId, ApiFixture.AdminEmail);

        Assert.Equal("student", profile.GetProperty("role").GetString());
        Assert.Equal(UserRole.Student, await RoleOfAsync(userId));
    }

    [Fact]
    public async Task Email_in_the_token_is_not_enough_when_the_provider_has_another_one()
    {
        // O token diz que o e-mail é o do administrador, mas a conta no provedor tem outro e-mail principal.
        var userId = Account("outra.pessoa@example.com", verified: true);

        var profile = await RegisterAsync(userId, ApiFixture.AdminEmail);

        Assert.Equal("student", profile.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Listed_email_stays_student_when_the_provider_does_not_answer()
    {
        var userId = ApiFixture.NewClerkUserId();

        var profile = await RegisterAsync(userId, ApiFixture.AdminEmail);

        Assert.Equal("student", profile.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Students_are_not_looked_up_in_the_provider()
    {
        var userId = Account("aluna@example.com", verified: true);

        var profile = await RegisterAsync(userId, "aluna@example.com");
        await api.CreateClient(api.CreateToken(userId, email: "aluna@example.com")).GetAsync("/v1/me");

        Assert.Equal("student", profile.GetProperty("role").GetString());
        Assert.DoesNotContain(userId, api.Directory.Lookups);
    }

    [Fact]
    public async Task Role_is_checked_again_on_each_login_and_not_inside_the_same_session()
    {
        var userId = Account(ApiFixture.AdminEmail, verified: false);
        await RegisterAsync(userId, ApiFixture.AdminEmail, sessionId: "sess_before");

        // O e-mail é verificado depois: na mesma sessão nada muda; no login seguinte, vira administrador.
        api.Directory.Accounts[userId] = new PrimaryEmail(ApiFixture.AdminEmail, Verified: true);
        Assert.Equal("student", await RoleInProfileAsync(api.Factory, userId, "sess_before"));
        Assert.Equal("admin", await RoleInProfileAsync(api.Factory, userId, "sess_after"));

        // E deixa de ser quando a verificação cai, também só no login seguinte.
        api.Directory.Accounts[userId] = new PrimaryEmail(ApiFixture.AdminEmail, Verified: false);
        Assert.Equal("admin", await RoleInProfileAsync(api.Factory, userId, "sess_after"));
        Assert.Equal("student", await RoleInProfileAsync(api.Factory, userId, "sess_later"));
    }

    [Fact]
    public async Task Email_removed_from_the_list_loses_access_at_once_and_the_role_on_next_login()
    {
        var userId = Account(ApiFixture.AdminEmail, verified: true);
        await RegisterAsync(userId, ApiFixture.AdminEmail, sessionId: "sess_listed");
        Assert.Equal(UserRole.Admin, await RoleOfAsync(userId));

        await using var withoutTheEmail = api.CreateFactory(builder => builder.UseSetting("Admin:Emails", "outro@redigiai.test"));

        // A conferência das rotas de administração olha a lista atual, mesmo com o papel ainda gravado.
        await AssertRefusedAsync(withoutTheEmail, userId, hasSecondFactor: true, "NOT_FOUND", 404);

        Assert.Equal("student", await RoleInProfileAsync(withoutTheEmail, userId, "sess_unlisted"));
        Assert.Equal(UserRole.Student, await RoleOfAsync(userId));
    }

    [Fact]
    public async Task Email_changed_in_the_provider_to_one_outside_the_list_loses_the_role()
    {
        var userId = Account(ApiFixture.AdminEmail, verified: true);
        await RegisterAsync(userId, ApiFixture.AdminEmail);

        var webhook = api.CreateClerkWebhook(JsonSerializer.Serialize(new
        {
            type = "user.updated",
            data = new
            {
                id = userId,
                primary_email_address_id = "idn_1",
                email_addresses = new[] { new { id = "idn_1", email_address = "novo@example.com" } },
            },
        }));
        (await api.CreateClient().SendAsync(webhook)).EnsureSuccessStatusCode();

        Assert.Equal(UserRole.Student, await RoleOfAsync(userId));
    }

    [Fact]
    public async Task Admin_area_does_not_exist_for_students_and_asks_admins_for_the_second_factor()
    {
        var adminId = Account(ApiFixture.AdminEmail, verified: true);
        await RegisterAsync(adminId, ApiFixture.AdminEmail);
        var studentId = ApiFixture.NewClerkUserId();
        await RegisterAsync(studentId, "aluno@example.com");

        await AssertRefusedAsync(api.Factory, studentId, hasSecondFactor: true, "NOT_FOUND", 404);
        await AssertRefusedAsync(api.Factory, ApiFixture.NewClerkUserId(), hasSecondFactor: true, "NOT_FOUND", 404);
        await AssertRefusedAsync(api.Factory, adminId, hasSecondFactor: false, "ADMIN_2FA_REQUIRED", 403);

        var admin = await api.QueryAsync(db => db.Users.SingleAsync(u => u.ClerkUserId == adminId));
        Assert.Equal(admin.Id, await RequireAdminAsync(api.Factory, adminId, hasSecondFactor: true));
    }

    [Fact]
    public async Task Second_factor_can_be_switched_off_by_configuration()
    {
        var adminId = Account(ApiFixture.AdminEmail, verified: true);
        await RegisterAsync(adminId, ApiFixture.AdminEmail);
        await using var withoutSecondFactor = api.CreateFactory(builder => builder.UseSetting("Admin:Require2fa", "false"));

        await RequireAdminAsync(withoutSecondFactor, adminId, hasSecondFactor: false);
    }

    private string Account(string email, bool verified)
    {
        var userId = ApiFixture.NewClerkUserId();
        api.Directory.Accounts[userId] = new PrimaryEmail(email, verified);
        return userId;
    }

    private async Task<JsonElement> RegisterAsync(string userId, string email, string? sessionId = null)
    {
        var client = api.CreateClient(api.CreateToken(userId, email: email, sessionId: sessionId));
        var response = await client.PostAsJsonAsync("/v1/me", Adult);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<string?> RoleInProfileAsync(WebApplicationFactory<Program> factory, string userId, string sessionId)
    {
        var client = ApiFixture.CreateClient(factory, api.CreateToken(userId, email: ApiFixture.AdminEmail, sessionId: sessionId));
        var profile = await client.GetFromJsonAsync<JsonElement>("/v1/me");
        return profile.GetProperty("role").GetString();
    }

    private Task<UserRole> RoleOfAsync(string userId) =>
        api.QueryAsync(db => db.Users.Where(u => u.ClerkUserId == userId).Select(u => u.Role).SingleAsync());

    private Task<List<string>> AuditActionsAsync(string userId) =>
        api.QueryAsync(db => db.AuditLog
            .Where(a => db.Users.Any(u => u.Id == a.EntityId && u.ClerkUserId == userId))
            .Select(a => a.Action)
            .ToListAsync());

    private static async Task<Guid> RequireAdminAsync(WebApplicationFactory<Program> factory, string userId, bool hasSecondFactor)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IAdminAccessService>()
            .RequireAdminAsync(userId, hasSecondFactor, CancellationToken.None);
    }

    private static async Task AssertRefusedAsync(
        WebApplicationFactory<Program> factory, string userId, bool hasSecondFactor, string code, int status)
    {
        var error = await Assert.ThrowsAsync<AppError>(() => RequireAdminAsync(factory, userId, hasSecondFactor));
        Assert.Equal(code, error.Code);
        Assert.Equal(status, error.HttpStatus);
    }
}
