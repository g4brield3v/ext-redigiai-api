using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RedigiAI.Api.Http;
using RedigiAI.Application.Catalog;
using RedigiAI.Domain.Common;

namespace RedigiAI.Api.Controllers;

/// <summary>Catálogo público: vestibulares, temas e grades de correção.</summary>
[ApiController]
[AllowAnonymous]
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

    /// <summary>Temas ativos do vestibular, com os tipos aceitos e o número de textos da coletânea.</summary>
    [HttpGet("exams/{slug}/themes", Name = "listThemes")]
    [ProducesResponseType<IReadOnlyList<ThemeSummaryDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListThemes(string slug, [FromQuery] string? q, CancellationToken ct) =>
        Ok(await catalog.ListThemesAsync(slug, q, ct) ?? throw Errors.NotFound());

    /// <summary>Enunciado, coletânea e tipos de redação aceitos pelo tema.</summary>
    [HttpGet("themes/{id:guid}", Name = "getTheme")]
    [ProducesResponseType<ThemeDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTheme(Guid id, CancellationToken ct) =>
        Ok(await catalog.GetThemeAsync(id, ct) ?? throw Errors.ThemeNotAvailable());

    /// <summary>Critérios, pesos e descritores de uma versão da grade.</summary>
    [HttpGet("rubrics/{versionId:guid}", Name = "getRubric")]
    [ProducesResponseType<RubricDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRubric(Guid versionId, CancellationToken ct) =>
        Ok(await catalog.GetRubricAsync(versionId, ct) ?? throw Errors.NotFound());
}
