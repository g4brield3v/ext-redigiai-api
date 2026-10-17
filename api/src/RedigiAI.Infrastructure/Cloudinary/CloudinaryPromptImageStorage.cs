using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RedigiAI.Application.Abstractions;

namespace RedigiAI.Infrastructure.Cloudinary;

/// <summary>
/// Imagens de proposta no Cloudinary, em entrega pública (tipo <c>upload</c>). O arquivo sai do backend, com a
/// requisição assinada; o navegador do administrador nunca fala com o Cloudinary.
/// </summary>
internal sealed class CloudinaryPromptImageStorage(
    IOptions<CloudinaryOptions> options, ILogger<CloudinaryPromptImageStorage> logger) : IPromptImageStorage
{
    private const string DeliveryType = "upload";

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

    public string PublicUrl(string publicId) =>
        $"https://res.cloudinary.com/{Uri.EscapeDataString(Settings().CloudName)}/image/{DeliveryType}/"
        + string.Join('/', publicId.Split('/').Select(Uri.EscapeDataString));

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
