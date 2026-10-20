using Microsoft.AspNetCore.Mvc.Filters;
using RedigiAI.Application.Accounts;
using RedigiAI.Domain.Common;

namespace RedigiAI.Api.Auth;

/// <summary>
/// Rotas de administração (<c>/v1/admin/*</c>): além do token, exigem a flag de administrador, conferida no banco a cada
/// requisição. Quem não é administrador recebe 404, antes de o corpo ser lido.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
internal sealed class AdminOnlyAttribute : Attribute, IAsyncAuthorizationFilter
{
    private const string AdminIdItem = "RedigiAI.AdminUserId";

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var http = context.HttpContext;
        var access = http.RequestServices.GetRequiredService<IAdminAccessService>();
        http.Items[AdminIdItem] = await access.RequireAdminAsync(http.User.GetClerkIdentity().UserId, http.RequestAborted);
    }

    /// <summary>O administrador que passou pela conferência desta requisição.</summary>
    public static Guid AdminIdOf(HttpContext http) =>
        http.Items[AdminIdItem] is Guid id ? id : throw Errors.NotFound();
}
