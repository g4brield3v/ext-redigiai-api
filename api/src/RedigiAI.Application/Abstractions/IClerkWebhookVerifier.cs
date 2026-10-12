namespace RedigiAI.Application.Abstractions;

/// <summary>Confere a assinatura (padrão Svix) de um webhook do Clerk.</summary>
public interface IClerkWebhookVerifier
{
    bool IsValid(string? id, string? timestamp, string? signatures, ReadOnlySpan<byte> body);
}
