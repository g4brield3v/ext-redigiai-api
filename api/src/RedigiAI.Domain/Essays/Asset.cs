using RedigiAI.Domain.Common;

namespace RedigiAI.Domain.Essays;

/// <summary>Foto privada guardada no provedor de arquivos. Nasce reservada, antes do upload, e é confirmada no envio.</summary>
public sealed class Asset
{
    public const string Cloudinary = "cloudinary";
    public const long MaxBytes = 10 * 1024 * 1024;
    public const string JpegMime = "image/jpeg";

    /// <summary>Upload que nunca virou redação é apagado depois disso.</summary>
    public static readonly TimeSpan UnconfirmedLifetime = TimeSpan.FromHours(24);

    private Asset() { }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string Provider { get; private set; } = null!;
    public string PublicId { get; private set; } = null!;
    public long? Bytes { get; private set; }
    public string? Mime { get; private set; }
    public DateTimeOffset? PurgeAfter { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Já pertence a uma redação: não pode ser usada em outra.</summary>
    public bool IsConfirmed => Bytes is not null;

    public static Asset Reserve(Guid userId, DateTimeOffset now)
    {
        var id = Guid.CreateVersion7();
        return new Asset
        {
            Id = id,
            UserId = userId,
            Provider = Cloudinary,
            PublicId = $"essays/{userId:N}/{id:N}",
            PurgeAfter = now + UnconfirmedLifetime,
            CreatedAt = now,
        };
    }

    /// <summary>Confere o arquivo que chegou ao provedor (regra 3: JPG de até 10 MB) e o liga à redação.</summary>
    /// <param name="format">Formato informado pelo provedor, como <c>jpg</c>.</param>
    public void Confirm(long bytes, string format)
    {
        var isJpeg = format.Equals("jpg", StringComparison.OrdinalIgnoreCase)
            || format.Equals("jpeg", StringComparison.OrdinalIgnoreCase);
        if (IsConfirmed || !isJpeg || bytes is < 1 or > MaxBytes)
        {
            throw Errors.InvalidFile();
        }

        Bytes = bytes;
        Mime = JpegMime;
        PurgeAfter = null;
    }
}
