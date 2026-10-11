using System.Text.Json;
using Microsoft.Extensions.Options;
using RedigiAI.Application.Accounts;
using RedigiAI.Domain.Common;
using RedigiAI.Infrastructure.Clerk;

namespace RedigiAI.Api.Endpoints;

internal static class ClerkWebhookEndpoint
{
    private const int MaxBodyBytes = 256 * 1024;

    public static void MapClerkWebhook(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/webhooks/clerk", async (
                HttpContext http,
                IOptions<ClerkOptions> clerk,
                SyncClerkEmailHandler syncEmail,
                TimeProvider time,
                ILoggerFactory loggers,
                CancellationToken ct) =>
            {
                var body = await ReadBodyAsync(http.Request, ct);
                var headers = http.Request.Headers;

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

                var logger = loggers.CreateLogger("RedigiAI.Api.ClerkWebhook");
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

                return Results.NoContent();
            })
            .AllowAnonymous()
            .ExcludeFromDescription();
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
