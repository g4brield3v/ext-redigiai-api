using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RedigiAI.Application.Abstractions;

namespace RedigiAI.Infrastructure.Cloudinary;

/// <summary>
/// Imagens de proposta no Cloudinary, privadas (tipo <c>authenticated</c>), como as fotos das redações: o envio sai do
/// backend, com a requisição assinada, e a leitura é pelo download privado com validade.
/// </summary>
internal sealed class CloudinaryPromptImageStorage(
    IOptions<CloudinaryOptions> options, ILogger<CloudinaryPromptImageStorage> logger) : IPromptImageStorage
{
    private const string DeliveryType = "authenticated";

    /// <summary>Como as imagens eram enviadas antes de 20/10/2026 (entrega pública).</summary>
    private const string PublicType = "upload";

    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = TimeSpan.FromSeconds(60),
    };

    public async Task<StoredImage> UploadAsync(string publicId, byte[] content, string contentType, CancellationToken ct)
    {
        var settings = Settings();
        var signed = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["allowed_formats"] = "jpg,png,webp",
            ["overwrite"] = "false",
            ["public_id"] = publicId,
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ["type"] = DeliveryType,
        };

        using var form = new MultipartFormDataContent();
        foreach (var (name, value) in signed)
        {
            form.Add(new StringContent(value), name);
        }

        form.Add(new StringContent(settings.ApiKey), "api_key");
        form.Add(new StringContent(CloudinaryPhotoStorage.Sign(signed, settings.ApiSecret)), "signature");
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", "image");

        using var response = await Http.PostAsync(
            $"https://api.cloudinary.com/v1_1/{Uri.EscapeDataString(settings.CloudName)}/image/upload", form, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Cloudinary refused a prompt image upload with status {Status}", (int)response.StatusCode);
            response.EnsureSuccessStatusCode();
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = document.RootElement;
        return new StoredImage(root.GetProperty("bytes").GetInt64(), root.GetProperty("width").GetInt32(), root.GetProperty("height").GetInt32());
    }

    public string ViewUrl(string publicId, DateTimeOffset now)
    {
        var settings = Settings();
        var signed = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["expires_at"] = (now + IPromptImageStorage.ViewLifetime).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            // WebP guarda a transparência das tirinhas e gráficos e é lido por todos os navegadores atuais.
            ["format"] = "webp",
            ["public_id"] = publicId,
            ["timestamp"] = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ["type"] = DeliveryType,
        };
        var query = string.Join('&', signed
            .Append(new("api_key", settings.ApiKey))
            .Append(new("signature", CloudinaryPhotoStorage.Sign(signed, settings.ApiSecret)))
            .Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        return $"https://api.cloudinary.com/v1_1/{Uri.EscapeDataString(settings.CloudName)}/image/download?{query}";
    }

    public async Task<bool> MakePrivateAsync(string publicId, CancellationToken ct)
    {
        var settings = Settings();
        // Renomear para o mesmo identificador trocando o tipo: o arquivo não é enviado de novo.
        var signed = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["from_public_id"] = publicId,
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ["to_public_id"] = publicId,
            ["to_type"] = DeliveryType,
            ["type"] = PublicType,
        };

        using var form = new MultipartFormDataContent();
        foreach (var (name, value) in signed)
        {
            form.Add(new StringContent(value), name);
        }

        form.Add(new StringContent(settings.ApiKey), "api_key");
        form.Add(new StringContent(CloudinaryPhotoStorage.Sign(signed, settings.ApiSecret)), "signature");
        using var response = await Http.PostAsync(
            $"https://api.cloudinary.com/v1_1/{Uri.EscapeDataString(settings.CloudName)}/image/rename", form, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Não existe como pública: já é privada (ou nunca existiu).
            return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Cloudinary refused to make a prompt image private with status {Status}", (int)response.StatusCode);
            response.EnsureSuccessStatusCode();
        }

        return true;
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
