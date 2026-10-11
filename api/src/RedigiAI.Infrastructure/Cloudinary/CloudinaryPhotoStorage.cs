using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RedigiAI.Application.Abstractions;

namespace RedigiAI.Infrastructure.Cloudinary;

public sealed class CloudinaryOptions
{
    public const string Section = "Cloudinary";

    public string CloudName { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string ApiSecret { get; set; } = "";
}

/// <summary>
/// Fotos privadas no Cloudinary (tipo <c>authenticated</c>). O navegador envia o arquivo direto, com a
/// assinatura gerada aqui; o segredo nunca sai do backend.
/// </summary>
internal sealed class CloudinaryPhotoStorage(IOptions<CloudinaryOptions> options) : IPhotoStorage
{
    private const string DeliveryType = "authenticated";

    // Um cliente para a vida do processo; as conexões são renovadas para acompanhar mudanças de DNS.
    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    public SignedUpload SignUpload(string publicId, DateTimeOffset now)
    {
        var settings = Settings();

        // Só o que está assinado é aceito pelo Cloudinary: um arquivo, com este id, JPG, sem sobrescrever.
        var signed = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["allowed_formats"] = "jpg",
            ["overwrite"] = "false",
            ["public_id"] = publicId,
            ["timestamp"] = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ["type"] = DeliveryType,
        };

        var fields = new Dictionary<string, string>(signed)
        {
            ["api_key"] = settings.ApiKey,
            ["signature"] = Sign(signed, settings.ApiSecret),
        };

        return new SignedUpload($"https://api.cloudinary.com/v1_1/{Uri.EscapeDataString(settings.CloudName)}/image/upload", fields);
    }

    public async Task<StoredPhoto?> FindAsync(string publicId, CancellationToken ct)
    {
        var settings = Settings();
        var path = string.Join('/', publicId.Split('/').Select(Uri.EscapeDataString));
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.cloudinary.com/v1_1/{Uri.EscapeDataString(settings.CloudName)}/resources/image/{DeliveryType}/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.ApiKey}:{settings.ApiSecret}")));

        using var response = await Http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = document.RootElement;
        return new StoredPhoto(root.GetProperty("bytes").GetInt64(), root.GetProperty("format").GetString() ?? "");
    }

    /// <summary>SHA-1 de "chave=valor&amp;…" em ordem alfabética, seguido do segredo (assinatura padrão do Cloudinary).</summary>
    internal static string Sign(SortedDictionary<string, string> parameters, string apiSecret)
    {
        var toSign = string.Join('&', parameters.Select(p => $"{p.Key}={p.Value}")) + apiSecret;
        return Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(toSign)));
    }

    private CloudinaryOptions Settings()
    {
        var settings = options.Value;
        if (settings.CloudName.Length == 0 || settings.ApiKey.Length == 0 || settings.ApiSecret.Length == 0)
        {
            throw new InvalidOperationException(
                "Cloudinary is not configured: set Cloudinary__CloudName, Cloudinary__ApiKey and Cloudinary__ApiSecret.");
        }

        return settings;
    }
}
