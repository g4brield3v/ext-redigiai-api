using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RedigiAI.Api.Auth;
using RedigiAI.Api.Http;
using RedigiAI.Application.Grading;

namespace RedigiAI.Api.Controllers;

/// <summary>Painel de progresso do aluno logado. Só as correções do usuário do token.</summary>
[ApiController]
[Authorize]
[Route("v1/me/progress")]
[Tags("Account")]
[Produces("application/json")]
[ProducesResponseType<ErrorResponse>(StatusCodes.Status401Unauthorized)]
public sealed class ProgressController(IProgressService progress) : ControllerBase
{
    /// <summary>
    /// A evolução da nota (as 10 correções mais recentes, da mais antiga para a mais recente, com a variação da
    /// última) e o foco de melhoria: o critério que mais tirou pontos nas 3 últimas correções sem nota zero.
    /// </summary>
    [HttpGet(Name = "getProgress")]
    [ProducesResponseType<ProgressDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(CancellationToken ct) =>
        Ok(await progress.GetAsync(User.GetClerkIdentity().UserId, ct));
}
