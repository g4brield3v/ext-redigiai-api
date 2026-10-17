using RedigiAI.Domain.Common;

namespace RedigiAI.Domain.Essays;

public enum AssetScope
{
    /// <summary>Foto de redação: privada, lida só por endereço assinado.</summary>
    EssayPhoto,

    /// <summary>Imagem da coletânea de uma proposta: pública, com endereço estável.</summary>
    PromptImage,
}

/// <summary>
/// Arquivo guardado no provedor. A foto de redação é privada: nasce reservada, antes do upload, e é confirmada no
/// envio. A imagem de proposta é pública e já nasce com o arquivo enviado pelo administrador.
/// </summary>
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
    public AssetScope Scope { get; private set; }
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

    /// <summary>Imagem de proposta, já enviada ao provedor pelo administrador.</summary>
    /// <param name="id">O mesmo id usado no nome do arquivo no provedor.</param>
    public static Asset ForPromptImage(Guid id, Guid adminId, string publicId, long bytes, string mime, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicId);
        ArgumentOutOfRangeException.ThrowIfLessThan(bytes, 1);

        return new Asset
        {
            Id = id,
            UserId = adminId,
            Provider = Cloudinary,
            PublicId = publicId,
            Scope = AssetScope.PromptImage,
            Bytes = bytes,
            Mime = mime,
            CreatedAt = now,
        };
    }

    /// <summary>Confere o arquivo que chegou ao provedor (regra 3: JPG de até 10 MB) e o liga à redação.</summary>
    /// <param name="format">Formato informado pelo provedor, como <c>jpg</c>.</param>
    public void Confirm(long bytes, string format)
    {
        var isJpeg = format.Equals("jpg", StringComparison.OrdinalIgnoreCase)
            || format.Equals("jpeg", StringComparison.OrdinalIgnoreCase);
        if (Scope != AssetScope.EssayPhoto || IsConfirmed || !isJpeg || bytes is < 1 or > MaxBytes)
        {
            throw Errors.InvalidFile();
        }

        Bytes = bytes;
        Mime = JpegMime;
        PurgeAfter = null;
    }
}
