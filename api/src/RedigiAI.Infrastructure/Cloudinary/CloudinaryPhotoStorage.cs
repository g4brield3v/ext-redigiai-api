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

    /// <summary>
    /// Versão para leitura pela IA: cabe em 1568 px de lado, que é o que o modelo aproveita, e pesa bem menos.
    /// </summary>
    private const string ReadingTransformation = "c_limit,h_1568,w_1568,q_85";

    // Um cliente para a vida do processo; as conexões são renovadas para acompanhar mudanças de DNS.
    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = TimeSpan.FromSeconds(30),
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

        return new SignedUpload($"{ApiBase(settings)}/image/upload", fields);
    }

    public async Task<StoredPhoto?> FindAsync(string publicId, CancellationToken ct)
    {
        var settings = Settings();
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{ApiBase(settings)}/resources/image/{DeliveryType}/{EscapePath(publicId)}");
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

    public string CreateDownloadUrl(string publicId, DateTimeOffset now, TimeSpan validFor)
    {
        var settings = Settings();
        var signed = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["expires_at"] = (now + validFor).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ["format"] = "jpg",
            ["public_id"] = publicId,
            ["timestamp"] = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ["type"] = DeliveryType,
        };

        var query = string.Join('&', signed
            .Append(new("api_key", settings.ApiKey))
            .Append(new("signature", Sign(signed, settings.ApiSecret)))
            .Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));

        return $"{ApiBase(settings)}/image/download?{query}";
    }

    public async Task<byte[]> DownloadForReadingAsync(string publicId, CancellationToken ct)
    {
        var settings = Settings();

        // URL de entrega assinada: sem a assinatura, o Cloudinary não entrega imagem "authenticated".
        var toSign = $"{ReadingTransformation}/{publicId}.jpg";
        var digest = SHA1.HashData(Encoding.UTF8.GetBytes(toSign + settings.ApiSecret));
        var signature = Convert.ToBase64String(digest).Replace('+', '-').Replace('/', '_')[..8];
        var url = $"https://res.cloudinary.com/{Uri.EscapeDataString(settings.CloudName)}/image/{DeliveryType}"
            + $"/s--{signature}--/{ReadingTransformation}/{EscapePath(publicId)}.jpg";

        using var response = await Http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>SHA-1 de "chave=valor&amp;…" em ordem alfabética, seguido do segredo (assinatura padrão do Cloudinary).</summary>
    internal static string Sign(SortedDictionary<string, string> parameters, string apiSecret)
    {
        var toSign = string.Join('&', parameters.Select(p => $"{p.Key}={p.Value}")) + apiSecret;
        return Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(toSign)));
    }

    private static string ApiBase(CloudinaryOptions settings) =>
        $"https://api.cloudinary.com/v1_1/{Uri.EscapeDataString(settings.CloudName)}";

    private static string EscapePath(string publicId) =>
        string.Join('/', publicId.Split('/').Select(Uri.EscapeDataString));

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
