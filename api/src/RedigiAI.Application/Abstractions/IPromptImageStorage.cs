namespace RedigiAI.Application.Abstractions;

/// <summary>O que o provedor guardou: tamanho do arquivo e dimensões da imagem.</summary>
public sealed record StoredImage(long Bytes, int Width, int Height);

/// <summary>
/// Imagens das propostas de redação (tirinhas, charges, gráficos): pasta pública do Cloudinary, com endereço
/// estável. As fotos das redações ficam em <see cref="IPhotoStorage"/> e continuam privadas.
/// </summary>
public interface IPromptImageStorage
{
    /// <summary>Envia a imagem com esse identificador, sem sobrescrever.</summary>
    Task<StoredImage> UploadAsync(string publicId, byte[] content, string contentType, CancellationToken ct);

    /// <summary>Endereço público e estável da imagem.</summary>
    string PublicUrl(string publicId);
}
