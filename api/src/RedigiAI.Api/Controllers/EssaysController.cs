using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using RedigiAI.Api.Auth;
using RedigiAI.Api.Http;
using RedigiAI.Application.Essays;

namespace RedigiAI.Api.Controllers;

public sealed record BatchEssayRequest(IReadOnlyList<BatchEssayItem>? Essays, bool? AiNoticeAccepted);

/// <summary>Envio e acompanhamento das redações do aluno logado.</summary>
[ApiController]
[Authorize]
[Route("v1")]
[Tags("Essays")]
[Produces("application/json")]
[ProducesResponseType<ErrorResponse>(StatusCodes.Status401Unauthorized)]
public sealed class EssaysController(IUploadService uploads, IEssayService essays) : ControllerBase
{
    /// <summary>
    /// Assinatura para o navegador enviar 1 foto JPG direto ao Cloudinary, em área privada.
    /// O <c>assetId</c> devolvido identifica a foto no envio da redação.
    /// </summary>
    [HttpPost("uploads/sign", Name = "signUpload")]
    [ProducesResponseType<UploadSignatureDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SignUpload(CancellationToken ct) =>
        Ok(await uploads.SignAsync(User.GetClerkIdentity().UserId, ct));

    /// <summary>
    /// Confirma o envio de uma ou várias redações (tudo ou nada): reserva 1 correção por redação e inicia a
    /// transcrição. Exige <c>Idempotency-Key</c>; repetir a chave devolve as mesmas redações.
    /// </summary>
    [HttpPost("essays/batch", Name = "submitEssays")]
    [EnableRateLimiting(ApiHost.EssayBatchLimit)]
    [ProducesResponseType<BatchEssayResponseDto>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status402PaymentRequired)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> SubmitBatch(
        [FromBody] BatchEssayRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct) =>
        Accepted(await essays.SubmitBatchAsync(
            new SubmitEssaysCommand(
                User.GetClerkIdentity().UserId,
                idempotencyKey,
                request.Essays,
                request.AiNoticeAccepted == true,
                HttpContext.GetRequestOrigin()),
            ct));

    /// <summary>
    /// Redações do aluno, da mais recente para a mais antiga, com o título "Vestibular – Tipo – Tema".
    /// <c>status</c> filtra por situação (como <c>completed</c>) e <c>cursor</c> é o <c>nextCursor</c> da página anterior.
    /// </summary>
    [HttpGet("essays", Name = "listEssays")]
    [ProducesResponseType<EssayListDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] string? cursor, CancellationToken ct) =>
        Ok(await essays.ListAsync(User.GetClerkIdentity().UserId, status, cursor, ct));

    /// <summary>Situação da redação, prazo de revisão e, se houver, as palavras ilegíveis.</summary>
    [HttpGet("essays/{id:guid}", Name = "getEssay")]
    [ProducesResponseType<EssayDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        Ok(await essays.GetAsync(User.GetClerkIdentity().UserId, id, ct));
}
