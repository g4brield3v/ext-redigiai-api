using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using RedigiAI.Application.Abstractions;

namespace RedigiAI.Infrastructure.MercadoPago;

/// <summary>
/// A assinatura das notificações do Mercado Pago: <c>x-signature: ts=…,v1=…</c>, em que <c>v1</c> é o HMAC-SHA256,
/// com a chave secreta, do modelo <c>id:{data.id};request-id:{x-request-id};ts:{ts};</c>.
/// </summary>
internal sealed class MercadoPagoWebhookVerifier(IOptions<MercadoPagoOptions> options) : IPaymentWebhookVerifier
{
    public bool IsValid(string? signature, string? requestId, string resourceId) =>
        IsValid(options.Value.WebhookSecret, signature, requestId, resourceId);

    internal static bool IsValid(string secret, string? signature, string? requestId, string resourceId)
    {
        if (secret.Length == 0 || string.IsNullOrEmpty(signature))
        {
            return false;
        }

        string? ts = null;
        string? v1 = null;
        foreach (var part in signature.Split(','))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2)
            {
                continue;
            }

            switch (pair[0])
            {
                case "ts":
                    ts = pair[1];
                    break;
                case "v1":
                    v1 = pair[1];
                    break;
            }
        }

        if (string.IsNullOrEmpty(ts) || string.IsNullOrEmpty(v1) || v1.Length != 64)
        {
            return false;
        }

        // O id alfanumérico entra em minúsculas; uma parte ausente sai do modelo.
        var manifest = new StringBuilder($"id:{resourceId.ToLowerInvariant()};");
        if (!string.IsNullOrEmpty(requestId))
        {
            manifest.Append($"request-id:{requestId};");
        }

        manifest.Append($"ts:{ts};");
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(manifest.ToString()));
        byte[] received;
        try
        {
            received = Convert.FromHexString(v1);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expected, received);
    }
}
