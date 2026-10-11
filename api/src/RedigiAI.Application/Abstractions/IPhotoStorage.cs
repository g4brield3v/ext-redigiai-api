namespace RedigiAI.Application.Abstractions;

/// <summary>Para onde o navegador envia a foto e os campos assinados que vão junto com o arquivo.</summary>
public sealed record SignedUpload(string UploadUrl, IReadOnlyDictionary<string, string> Fields);

/// <param name="Format">Formato detectado pelo provedor, como <c>jpg</c>.</param>
public sealed record StoredPhoto(long Bytes, string Format);

/// <summary>Armazenamento privado das fotos das redações (Cloudinary).</summary>
public interface IPhotoStorage
{
    /// <summary>Assina o upload de exatamente um arquivo, com esse identificador, sem sobrescrita.</summary>
    SignedUpload SignUpload(string publicId, DateTimeOffset now);

    /// <summary>O que de fato chegou ao provedor; <c>null</c> se nada foi enviado com esse identificador.</summary>
    Task<StoredPhoto?> FindAsync(string publicId, CancellationToken ct);
}
