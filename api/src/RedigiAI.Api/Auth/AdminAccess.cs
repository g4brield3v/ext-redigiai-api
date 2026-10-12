using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Filters;
using RedigiAI.Application.Accounts;
using RedigiAI.Domain.Common;

namespace RedigiAI.Api.Auth;

/// <summary>
/// Rotas de administração (<c>/v1/admin/*</c>): além do token, exigem o papel <c>admin</c>, conferido no banco a
/// cada requisição, e o segundo fator na sessão. Quem não é administrador recebe 404, antes de o corpo ser lido.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
internal sealed class AdminOnlyAttribute : Attribute, IAsyncAuthorizationFilter
{
    private const string AdminIdItem = "RedigiAI.AdminUserId";

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var http = context.HttpContext;
        var access = http.RequestServices.GetRequiredService<IAdminAccessService>();
        var identity = http.User.GetClerkIdentity();
        http.Items[AdminIdItem] = await access.RequireAdminAsync(
            identity.UserId, identity.SessionId, http.User.HasSecondFactor(), http.RequestAborted);
    }

    /// <summary>O administrador que passou pela conferência desta requisição.</summary>
    public static Guid AdminIdOf(HttpContext http) =>
        http.Items[AdminIdItem] is Guid id ? id : throw Errors.NotFound();
}

internal static class SecondFactor
{
    private const string Claim = "fva";

    /// <summary>
    /// A claim <c>fva</c> do token de sessão do Clerk traz os minutos desde a verificação do primeiro e do segundo
    /// fator; <c>-1</c> no segundo significa que a sessão não passou pela verificação em duas etapas.
    /// </summary>
    public static bool HasSecondFactor(this ClaimsPrincipal principal)
    {
        // O leitor de JWT entrega uma lista como uma claim por item; aceita também a lista num valor só.
        var values = principal.FindAll(Claim).Select(claim => claim.Value).ToList();
        if (values is [var single] && single.StartsWith('['))
        {
            try
            {
                values = [.. JsonSerializer.Deserialize<JsonElement[]>(single)!.Select(item => item.ToString())];
            }
            catch (JsonException)
            {
                return false;
            }
        }

        return values.Count == 2
            && int.TryParse(values[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
            && minutes >= 0;
    }
}
