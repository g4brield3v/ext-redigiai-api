using RedigiAI.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RedigiAI.Api.Http;
using RedigiAI.Application.Catalog;
using RedigiAI.Domain.Common;

namespace RedigiAI.Api.Controllers;

/// <summary>Catálogo: vestibulares, propostas de redação e grades de correção. Só para quem está logado.</summary>
[ApiController]
[Authorize]
[RegisteredOnly]
[Route("v1")]
[Tags("Catalog")]
[Produces("application/json")]
public sealed class CatalogController(ICatalogService catalog) : ControllerBase
{
    /// <summary>Vestibulares e status (ativo ou em breve).</summary>
    [HttpGet("exams", Name = "listExams")]
    [ProducesResponseType<IReadOnlyList<ExamDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListExams(CancellationToken ct) =>
        Ok(await catalog.ListExamsAsync(ct));

    /// <summary>
    /// Propostas publicadas do vestibular, com o título, o resumo, o tipo de redação e o número de textos da
    /// coletânea. <c>q</c> filtra pelo título.
    /// </summary>
    [HttpGet("exams/{slug}/prompts", Name = "listPrompts")]
    [ProducesResponseType<IReadOnlyList<PromptSummaryDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListPrompts(string slug, [FromQuery] string? q, CancellationToken ct) =>
        Ok(await catalog.ListPromptsAsync(slug, q, ct) ?? throw Errors.NotFound());

    /// <summary>
    /// A versão publicada da proposta, com os blocos na ordem da página, o tipo de redação, as linhas e os endereços
    /// das imagens. Com <c>version</c>, devolve essa versão se ela já foi publicada: é o que a redação feita numa
    /// versão antiga continua mostrando.
    /// </summary>
    [HttpGet("prompts/{slug}", Name = "getPrompt")]
    [ProducesResponseType<PromptDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPrompt(string slug, [FromQuery] int? version, CancellationToken ct) =>
        Ok(await catalog.GetPromptAsync(slug, version, ct) ?? throw Errors.PromptNotAvailable());

    /// <summary>Critérios, pesos e descritores de uma versão da grade.</summary>
    [HttpGet("rubrics/{versionId:guid}", Name = "getRubric")]
    [ProducesResponseType<RubricDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRubric(Guid versionId, CancellationToken ct) =>
        Ok(await catalog.GetRubricAsync(versionId, ct) ?? throw Errors.NotFound());
}
