using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RedigiAI.Application.Abstractions;

namespace RedigiAI.Infrastructure.Clerk;

/// <summary>
/// Backend API do Clerk. Usada só para saber se o e-mail principal de uma conta foi verificado: o token de sessão
/// não traz essa informação.
/// </summary>
internal sealed class ClerkBackendApi(IOptions<ClerkOptions> options, ILogger<ClerkBackendApi> logger) : IIdentityDirectory
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    public async Task<PrimaryEmail?> FindPrimaryEmailAsync(string clerkUserId, CancellationToken ct)
    {
        var clerk = options.Value;
        if (clerk.SecretKey.Length == 0)
        {
            logger.LogError("Clerk Backend API is not configured: set Clerk__SecretKey");
            return null;
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{clerk.BackendApiUrl.TrimEnd('/')}/v1/users/{Uri.EscapeDataString(clerkUserId)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", clerk.SecretKey);

        try
        {
            using var response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Clerk user lookup answered {Status}", (int)response.StatusCode);
                return null;
            }

            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: ct);
            return ClerkUserReader.ReadPrimaryEmail(document.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning("Clerk user lookup did not complete: {Error}", ex.GetType().Name);
            return null;
        }
    }
}

/// <summary>Leitura do usuário devolvido pela Backend API do Clerk.</summary>
public static class ClerkUserReader
{
    /// <summary>Lê <c>primary_email_address_id</c> e o endereço correspondente, com <c>verification.status</c>.</summary>
    public static PrimaryEmail? ReadPrimaryEmail(JsonElement user)
    {
        if (user.ValueKind != JsonValueKind.Object
            || !user.TryGetProperty("primary_email_address_id", out var primaryId)
            || primaryId.ValueKind != JsonValueKind.String
            || !user.TryGetProperty("email_addresses", out var addresses)
            || addresses.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var address in addresses.EnumerateArray())
        {
            if (address.ValueKind == JsonValueKind.Object
                && address.TryGetProperty("id", out var id)
                && id.ValueEquals(primaryId.GetString())
                && address.TryGetProperty("email_address", out var value)
                && value.GetString() is { Length: > 0 } email)
            {
                var verified = address.TryGetProperty("verification", out var verification)
                    && verification.ValueKind == JsonValueKind.Object
                    && verification.TryGetProperty("status", out var status)
                    && status.ValueEquals("verified");
                return new PrimaryEmail(email, verified);
            }
        }

        return null;
    }
}
