namespace RedigiAI.Application.Abstractions;

/// <summary>O que o provedor guardou: tamanho do arquivo e dimensões da imagem.</summary>
public sealed record StoredImage(long Bytes, int Width, int Height);

/// <summary>
/// Imagens das propostas de redação (tirinhas, charges, gráficos). Privadas, como as fotos das redações: só abrem por
/// um endereço assinado que vence, entregue a quem está logado (decisão de 20/10/2026: nenhum conteúdo sem login).
/// </summary>
public interface IPromptImageStorage
{
    /// <summary>Quanto vale o endereço de uma imagem: o tempo de ler a proposta.</summary>
    public static readonly TimeSpan ViewLifetime = TimeSpan.FromHours(1);

    /// <summary>Envia a imagem com esse identificador, privada e sem sobrescrever.</summary>
    Task<StoredImage> UploadAsync(string publicId, byte[] content, string contentType, CancellationToken ct);

    /// <summary>Endereço assinado da imagem, que vence em <see cref="ViewLifetime"/>.</summary>
    string ViewUrl(string publicId, DateTimeOffset now);

    /// <summary>Torna privada uma imagem enviada antes como pública. Já privada (ou inexistente): não faz nada.</summary>
    /// <returns>Se mudou.</returns>
    Task<bool> MakePrivateAsync(string publicId, CancellationToken ct);
}
