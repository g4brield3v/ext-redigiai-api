using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RedigiAI.Infrastructure.Clerk;

/// <summary>
/// Confere a assinatura dos webhooks do Clerk (padrão Svix): HMAC-SHA256 de "{id}.{timestamp}.{corpo}".
/// </summary>
public static class SvixSignatureVerifier
{
    private const string SecretPrefix = "whsec_";
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    public static bool IsValid(
        string? secret, string? id, string? timestamp, string? signatures, ReadOnlySpan<byte> body, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(id) || string.IsNullOrEmpty(signatures)
            || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        // Fora da janela, a mensagem pode ser uma repetição de outra já aceita.
        if ((now - DateTimeOffset.FromUnixTimeSeconds(seconds)).Duration() > Tolerance)
        {
            return false;
        }

        var key = new byte[secret.Length];
        var encodedKey = secret.StartsWith(SecretPrefix, StringComparison.Ordinal) ? secret[SecretPrefix.Length..] : secret;
        if (!Convert.TryFromBase64String(encodedKey, key, out var keyLength))
        {
            return false;
        }

        var prefix = Encoding.UTF8.GetBytes($"{id}.{timestamp}.");
        var signed = new byte[prefix.Length + body.Length];
        prefix.CopyTo(signed, 0);
        body.CopyTo(signed.AsSpan(prefix.Length));
        var expected = HMACSHA256.HashData(key.AsSpan(0, keyLength), signed);

        var candidate = new byte[expected.Length];
        foreach (var signature in signatures.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (signature.StartsWith("v1,", StringComparison.Ordinal)
                && Convert.TryFromBase64String(signature[3..], candidate, out var length)
                && length == expected.Length
                && CryptographicOperations.FixedTimeEquals(candidate, expected))
            {
                return true;
            }
        }

        return false;
    }
}
