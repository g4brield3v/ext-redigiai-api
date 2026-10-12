using RedigiAI.Domain.Essays;

namespace RedigiAI.Application.Abstractions;

/// <param name="Readable">Falso quando a foto não permite ler a redação (desfocada, cortada, não é uma folha).</param>
public sealed record TranscriptionReading(IReadOnlyList<ReadLine> Lines, bool IdentificationFound, bool Readable);

public sealed record AiTokens(int Input, int Output, int CacheRead, int CacheWrite);

/// <param name="Reading">Nulo quando a resposta veio fora do formato combinado; o consumo é cobrado mesmo assim.</param>
public sealed record TranscriptionCall(TranscriptionReading? Reading, string Model, AiTokens Tokens);

/// <param name="Instructions">Instruções da versão ativa, já com os campos preenchidos.</param>
/// <param name="Model">Modelo definido pelo perfil de correção.</param>
public sealed record TranscriptionRequest(byte[] Jpeg, int SheetLines, string Instructions, string Model);

/// <summary>Lê a foto da folha e devolve o texto como está escrito, sem corrigir nada (regra 5).</summary>
public interface IEssayTranscriber
{
    /// <exception cref="Domain.Common.AppError">
    /// <c>AI_UNAVAILABLE</c> (retentável) para 429, 5xx, sobrecarga e tempo esgotado; erro não retentável quando a
    /// IA recusa o pedido.
    /// </exception>
    Task<TranscriptionCall> TranscribeAsync(TranscriptionRequest request, CancellationToken ct);
}
