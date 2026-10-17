using RedigiAI.Domain.Essays;

namespace RedigiAI.Application.Abstractions;

/// <param name="Readable">Falso quando a foto não permite ler a redação (desfocada, cortada, não é uma folha).</param>
public sealed record TranscriptionReading(IReadOnlyList<ReadLine> Lines, bool IdentificationFound, bool Readable);

public sealed record AiTokens(int Input, int Output, int CacheRead, int CacheWrite);

/// <summary>
/// A chamada falhou depois que a IA já tinha começado a responder (erro no meio do streaming, conexão perdida,
/// tempo esgotado). O que foi gerado até ali é cobrado: quem chamou grava <see cref="Tokens"/> no gasto da
/// redação e segue com <see cref="Error"/>.
/// </summary>
/// <param name="Tokens">
/// O consumo informado pela API; a saída que ela ainda não informou entra pelo teto do pedido (<c>max_tokens</c>),
/// para o limite de gasto nunca ficar por baixo.
/// </param>
public sealed class AiCallFailedException(Domain.Common.AppError error, AiTokens tokens) : Exception(error.Message, error)
{
    public Domain.Common.AppError Error { get; } = error;

    public AiTokens Tokens { get; } = tokens;
}

/// <param name="Reading">Nulo quando a resposta veio fora do formato combinado; o consumo é cobrado mesmo assim.</param>
/// <param name="Truncated">A resposta parou no <c>max_tokens</c>: a leitura é nula e nunca é completada.</param>
public sealed record TranscriptionCall(TranscriptionReading? Reading, string Model, AiTokens Tokens, bool Truncated = false);

/// <param name="Instructions">Instruções da versão ativa, já com os campos preenchidos.</param>
/// <param name="Model">Modelo definido pelo perfil de correção.</param>
/// <param name="MaxTokens">Teto de segurança da resposta (<c>max_tokens</c>).</param>
public sealed record TranscriptionRequest(byte[] Jpeg, int SheetLines, string Instructions, string Model, int MaxTokens);

/// <summary>Lê a foto da folha e devolve o texto como está escrito, sem corrigir nada (regra 5).</summary>
public interface IEssayTranscriber
{
    /// <exception cref="Domain.Common.AppError">
    /// <c>AI_UNAVAILABLE</c> (retentável) para 429, 5xx, sobrecarga e tempo esgotado; erro não retentável quando a
    /// IA recusa o pedido.
    /// </exception>
    Task<TranscriptionCall> TranscribeAsync(TranscriptionRequest request, CancellationToken ct);
}
