using System.Text.Json;
using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Common;

namespace RedigiAI.Application.Accounts;

/// <summary>Um webhook do Clerk como chegou: os cabeçalhos Svix e o corpo ainda em bytes.</summary>
public sealed record ClerkWebhookMessage(string? Id, string? Timestamp, string? Signatures, byte[] Body);

public enum ClerkWebhookOutcome
{
    EmailSynced,

    /// <summary>A exclusão e a anonimização entram com o job <c>delete_user</c> (etapa 5).</summary>
    DeletionNotImplemented,
    Ignored,
}

public sealed record ClerkWebhookResult(ClerkWebhookOutcome Outcome, string? EventType);

public interface IClerkWebhookService
{
    /// <summary>Confere a assinatura e aplica o evento. Assinatura inválida → <c>UNAUTHENTICATED</c>.</summary>
    Task<ClerkWebhookResult> ReceiveAsync(ClerkWebhookMessage message, CancellationToken ct);
}

internal sealed class ClerkWebhookService(IClerkWebhookVerifier verifier, IAccountService accounts) : IClerkWebhookService
{
    public async Task<ClerkWebhookResult> ReceiveAsync(ClerkWebhookMessage message, CancellationToken ct)
    {
        // A assinatura é conferida antes de interpretar ou gravar qualquer coisa.
        if (!verifier.IsValid(message.Id, message.Timestamp, message.Signatures, message.Body))
        {
            throw Errors.Unauthenticated();
        }

        using var document = JsonDocument.Parse(message.Body);
        var type = document.RootElement.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;

        switch (type)
        {
            case "user.updated" when TryReadPrimaryEmail(document.RootElement, out var clerkUserId, out var email):
                await accounts.SyncEmailAsync(clerkUserId, email, ct);
                return new ClerkWebhookResult(ClerkWebhookOutcome.EmailSynced, type);
            case "user.deleted":
                return new ClerkWebhookResult(ClerkWebhookOutcome.DeletionNotImplemented, type);
            default:
                return new ClerkWebhookResult(ClerkWebhookOutcome.Ignored, type);
        }
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
