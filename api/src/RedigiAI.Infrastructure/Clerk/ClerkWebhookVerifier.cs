using Microsoft.Extensions.Options;
using RedigiAI.Application.Abstractions;

namespace RedigiAI.Infrastructure.Clerk;

internal sealed class ClerkWebhookVerifier(IOptions<ClerkOptions> clerk, TimeProvider time) : IClerkWebhookVerifier
{
    public bool IsValid(string? id, string? timestamp, string? signatures, ReadOnlySpan<byte> body) =>
        SvixSignatureVerifier.IsValid(clerk.Value.WebhookSecret, id, timestamp, signatures, body, time.GetUtcNow());
}
