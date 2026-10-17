using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RedigiAI.Api.Auth;
using RedigiAI.Api.Http;
using RedigiAI.Application.Catalog;
using RedigiAI.Domain.Catalog;
using RedigiAI.Domain.Common;

namespace RedigiAI.Api.Controllers;

public sealed record ReorderBlocksRequest(IReadOnlyList<BlockPositionRequest>? Blocks);

public sealed record BlockPositionRequest(Guid? Id, int? Position);

/// <summary>
/// Administração das propostas de redação. Só para administradores: para os demais usuários, estas rotas respondem
/// 404, como se não existissem. Com a verificação em duas etapas exigida, a sessão sem o segundo fator recebe
/// <c>ADMIN_2FA_REQUIRED</c> (403).
/// </summary>
[ApiController]
[Authorize]
[AdminOnly]
[Route("v1/admin")]
[Tags("Admin")]
[Produces("application/json")]
[ProducesResponseType<ErrorResponse>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ErrorResponse>(StatusCodes.Status403Forbidden)]
[ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
public sealed class AdminPromptsController(IPromptAdminService prompts) : ControllerBase
{
    /// <summary>Arquivo mais os campos do formulário que o acompanham.</summary>
    private const long MaxUploadRequestBytes = 6 * 1024 * 1024;

    private Guid AdminId => AdminOnlyAttribute.AdminIdOf(HttpContext);

    /// <summary>
    /// Envia uma imagem de proposta (JPG, PNG ou WebP, até 5 MB) para a pasta pública. O <c>assetId</c> devolvido
    /// vai no bloco de imagem da coletânea.
    /// </summary>
    [HttpPost("assets", Name = "uploadPromptImage")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxUploadRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadRequestBytes)]
    [ProducesResponseType<PromptImageDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> UploadImage(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length is < 1 or > 5 * 1024 * 1024)
        {
            throw Errors.Validation("Envie uma imagem JPG, PNG ou WebP, com até 5 MB.");
        }

        using var content = new MemoryStream((int)file.Length);
        await file.CopyToAsync(content, ct);
        return Ok(await prompts.UploadImageAsync(AdminId, new PromptImageUpload(content.ToArray(), file.ContentType), ct));
    }

    /// <summary>
    /// As categorias de correção do vestibular, para a lista do cartão Instruções, com a indicação de quais têm
    /// perfil de correção ativo. Vestibular desconhecido responde 404.
    /// </summary>
    [HttpGet("exams/{slug}/essay-types", Name = "listEssayTypes")]
    [ProducesResponseType<IReadOnlyList<EssayTypeOptionDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListEssayTypes(string slug, CancellationToken ct) =>
        Ok(await prompts.ListEssayTypesAsync(slug, ct));

    /// <summary>
    /// Cria um rascunho, vazio ou a partir do JSON completo da proposta (importação). Exige <c>Idempotency-Key</c>;
    /// repetir a chave devolve o mesmo rascunho.
    /// </summary>
    [HttpPost("prompts", Name = "createPrompt")]
    [ProducesResponseType<PromptSavedDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<PromptSavedDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create(
        [FromBody] PromptDocument? document,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        var result = await prompts.CreateAsync(AdminId, idempotencyKey, document, ct);
        return result.Created ? Created($"/v1/admin/prompts/{result.Prompt.Id}", result.Prompt) : Ok(result.Prompt);
    }

    /// <summary>
    /// Todas as versões, com situação, tipo, número de textos e data de atualização. <c>status</c> filtra por
    /// situação (<c>draft</c>, <c>published</c>, <c>archived</c>), <c>exam</c> pelo vestibular e <c>q</c> pelo título.
    /// </summary>
    [HttpGet("prompts", Name = "listAdminPrompts")]
    [ProducesResponseType<PromptListDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> List(
        [FromQuery] string? status, [FromQuery] string? exam, [FromQuery] string? q, CancellationToken ct) =>
        Ok(await prompts.ListAsync(status, exam, q, ct));

    /// <summary>A proposta no formato de cadastro, o mesmo da importação. É o que "Exportar JSON" entrega.</summary>
    [HttpGet("prompts/{id:guid}", Name = "getAdminPrompt")]
    [ProducesResponseType<PromptAdminDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Ok(await prompts.GetAsync(id, ct));

    /// <summary>
    /// Substitui o rascunho inteiro. Proposta publicada responde <c>PROMPT_NOT_DRAFT</c>; com
    /// <c>expectedUpdatedAt</c>, se houve outro salvamento antes, <c>PROMPT_CHANGED</c>.
    /// </summary>
    [HttpPut("prompts/{id:guid}", Name = "savePrompt")]
    [ProducesResponseType<PromptSavedDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Save(Guid id, [FromBody] PromptDocument document, CancellationToken ct) =>
        Ok(await prompts.SaveAsync(AdminId, id, document, ct));

    /// <summary>Muda só a ordem dos blocos do rascunho. Posição repetida responde <c>VALIDATION_FAILED</c>.</summary>
    [HttpPatch("prompts/{id:guid}/order", Name = "reorderPromptBlocks")]
    [ProducesResponseType<PromptSavedDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Reorder(Guid id, [FromBody] ReorderBlocksRequest request, CancellationToken ct)
    {
        var blocks = request.Blocks ?? [];
        if (blocks.Any(block => block?.Id is null || block.Position is null))
        {
            throw Errors.ValidationFailed([new ValidationIssue("blocks", "Informe o id e a posição de cada bloco.")]);
        }

        return Ok(await prompts.ReorderAsync(AdminId, id, [.. blocks.Select(block => new BlockPosition(block.Id!.Value, block.Position!.Value))], ct));
    }

    /// <summary>A validação de publicação, sem publicar.</summary>
    [HttpPost("prompts/{id:guid}/validate", Name = "validatePrompt")]
    [ProducesResponseType<PromptValidationDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Validate(Guid id, CancellationToken ct) => Ok(await prompts.ValidateAsync(id, ct));

    /// <summary>Valida e publica. A versão publicada anterior do mesmo endereço passa a arquivada.</summary>
    [HttpPost("prompts/{id:guid}/publish", Name = "publishPrompt")]
    [ProducesResponseType<PromptSavedDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Publish(Guid id, CancellationToken ct) => Ok(await prompts.PublishAsync(AdminId, id, ct));

    /// <summary>Cria um rascunho da versão seguinte, com o conteúdo desta versão publicada ou arquivada.</summary>
    [HttpPost("prompts/{id:guid}/new-version", Name = "createPromptVersion")]
    [ProducesResponseType<PromptSavedDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> NewVersion(Guid id, CancellationToken ct)
    {
        var draft = await prompts.NewVersionAsync(AdminId, id, ct);
        return Created($"/v1/admin/prompts/{draft.Id}", draft);
    }

    /// <summary>Tira a proposta da lista dos alunos. Nada é apagado.</summary>
    [HttpPost("prompts/{id:guid}/archive", Name = "archivePrompt")]
    [ProducesResponseType<PromptSavedDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Archive(Guid id, CancellationToken ct) => Ok(await prompts.ArchiveAsync(AdminId, id, ct));

    /// <summary>Apaga só rascunho. Versão publicada ou arquivada responde <c>PROMPT_NOT_DRAFT</c>.</summary>
    [HttpDelete("prompts/{id:guid}", Name = "deletePrompt")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await prompts.DeleteAsync(AdminId, id, ct);
        return NoContent();
    }
}
