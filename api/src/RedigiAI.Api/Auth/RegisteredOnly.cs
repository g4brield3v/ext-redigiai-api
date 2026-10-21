using Microsoft.AspNetCore.Mvc.Filters;
using RedigiAI.Application.Accounts;

namespace RedigiAI.Api.Auth;

/// <summary>
/// Conteúdo da plataforma: além do token do Clerk, exige a conta na plataforma (cadastro concluído). Quem só criou a
/// conta no Clerk, ou foi recusado pela idade, recebe <c>PROFILE_NOT_FOUND</c> antes de qualquer leitura.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
internal sealed class RegisteredOnlyAttribute : Attribute, IAsyncAuthorizationFilter
{
    public Task OnAuthorizationAsync(AuthorizationFilterContext context) =>
        context.HttpContext.RequestServices.GetRequiredService<IRegisteredAccess>()
            .RequireRegisteredAsync(context.HttpContext.User.GetClerkIdentity().UserId, context.HttpContext.RequestAborted);
}
