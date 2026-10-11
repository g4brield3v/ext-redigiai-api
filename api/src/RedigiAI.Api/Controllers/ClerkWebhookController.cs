using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RedigiAI.Api.Http;
using RedigiAI.Application.Accounts;
using RedigiAI.Domain.Common;
using RedigiAI.Infrastructure.Clerk;

namespace RedigiAI.Api.Controllers;

/// <summary>Eventos de usuário enviados pelo Clerk. Sem token: a autenticação é a assinatura Svix.</summary>
[ApiController]
[AllowAnonymous]
[Route("v1/webhooks/clerk")]
[Tags("Webhooks")]
public sealed class ClerkWebhookController(
    IOptions<ClerkOptions> clerk,
    SyncClerkEmailHandler syncEmail,
    TimeProvider time,
    ILogger<ClerkWebhookController> logger) : ControllerBase
{
    private const int MaxBodyBytes = 256 * 1024;

    /// <summary>Recebe um evento do Clerk (cabeçalhos <c>svix-id</c>, <c>svix-timestamp</c> e <c>svix-signature</c>).</summary>
    [HttpPost(Name = "receiveClerkWebhook")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        var body = await ReadBodyAsync(Request, ct);
        var headers = Request.Headers;

        // A assinatura é conferida antes de interpretar ou gravar qualquer coisa.
        if (body is null || !SvixSignatureVerifier.IsValid(
                clerk.Value.WebhookSecret,
                headers["svix-id"],
                headers["svix-timestamp"],
                headers["svix-signature"],
                body,
                time.GetUtcNow()))
        {
            throw Errors.Unauthenticated();
        }

        using var document = JsonDocument.Parse(body);
        var type = document.RootElement.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;

        switch (type)
        {
            case "user.updated" when TryReadPrimaryEmail(document.RootElement, out var clerkUserId, out var email):
                await syncEmail.HandleAsync(clerkUserId, email, ct);
                break;
            case "user.deleted":
                // A exclusão e a anonimização entram com o job delete_user (etapa 5).
                logger.LogWarning("Clerk user.deleted received; local deletion is not implemented yet");
                break;
            default:
                logger.LogInformation("Clerk webhook {Type} ignored", type);
                break;
        }

        return NoContent();
    }

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

    private static bool TryReadPrimaryEmail(JsonElement root, out string clerkUserId, out string email)
    {
        clerkUserId = email = "";
        if (!root.TryGetProperty("data", out var data)
            || !data.TryGetProperty("id", out var id)
            || !data.TryGetProperty("primary_email_address_id", out var primaryId)
            || !data.TryGetProperty("email_addresses", out var addresses)
            || addresses.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var address in addresses.EnumerateArray())
        {
            if (address.TryGetProperty("id", out var addressId)
                && addressId.ValueEquals(primaryId.GetString())
                && address.TryGetProperty("email_address", out var value)
                && value.GetString() is { Length: > 0 } found
                && id.GetString() is { Length: > 0 } userId)
            {
                clerkUserId = userId;
                email = found;
                return true;
            }
        }

        return false;
    }
}
