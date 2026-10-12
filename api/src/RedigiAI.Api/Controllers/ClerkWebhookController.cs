using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RedigiAI.Api.Http;
using RedigiAI.Application.Accounts;
using RedigiAI.Domain.Common;

namespace RedigiAI.Api.Controllers;

/// <summary>Eventos de usuário enviados pelo Clerk. Sem token: a autenticação é a assinatura Svix.</summary>
[ApiController]
[AllowAnonymous]
[Route("v1/webhooks/clerk")]
[Tags("Webhooks")]
public sealed class ClerkWebhookController(IClerkWebhookService webhooks, ILogger<ClerkWebhookController> logger)
    : ControllerBase
{
    private const int MaxBodyBytes = 256 * 1024;

    /// <summary>Recebe um evento do Clerk (cabeçalhos <c>svix-id</c>, <c>svix-timestamp</c> e <c>svix-signature</c>).</summary>
    [HttpPost(Name = "receiveClerkWebhook")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        var body = await ReadBodyAsync(Request, ct) ?? throw Errors.Unauthenticated();
        var headers = Request.Headers;

        var result = await webhooks.ReceiveAsync(
            new ClerkWebhookMessage(headers["svix-id"], headers["svix-timestamp"], headers["svix-signature"], body), ct);

        switch (result.Outcome)
        {
            case ClerkWebhookOutcome.DeletionNotImplemented:
                logger.LogWarning("Clerk user.deleted received; local deletion is not implemented yet");
                break;
            case ClerkWebhookOutcome.Ignored:
                logger.LogInformation("Clerk webhook {Type} ignored", result.EventType);
                break;
        }

        return NoContent();
    }

    /// <returns><c>null</c> se o corpo passa do limite.</returns>
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
