using System.Text.Json.Serialization;
using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Essays;

namespace RedigiAI.Application.Essays;

/// <param name="Fields">Campos que o navegador envia ao provedor junto com o arquivo.</param>
public sealed record UploadSignatureDto(Guid AssetId, string UploadUrl, IReadOnlyDictionary<string, string> Fields);

/// <summary>
/// Uma redação do envio. Sem tipo: o backend usa o da proposta. Qualquer campo a mais (um tipo, por exemplo) recusa
/// o pedido inteiro.
/// </summary>
/// <param name="PromptId">A versão publicada da proposta; numa reescrita vale a versão da redação original.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BatchEssayItem(Guid PromptId, Guid AssetId, Guid? ParentEssayId);

public sealed record SubmitEssaysCommand(
    string ClerkUserId,
    string? IdempotencyKey,
    IReadOnlyList<BatchEssayItem>? Essays,
    bool AiNoticeAccepted,
    RequestOrigin Origin);

public sealed record BatchEssayResponseDto(IReadOnlyList<Guid> EssayIds);

/// <summary>O que o aluno digitou para uma palavra ilegível.</summary>
public sealed record IllegibleAnswer(Guid WordId, string? Value);

/// <param name="Url">Endereço temporário da foto, válido por 5 minutos.</param>
public sealed record PhotoUrlDto(string Url);

/// <param name="Title">"Vestibular – Tipo – Tema", gerado pelo backend.</param>
/// <param name="PromptSlug">Endereço da proposta (<c>/propostas/{slug}</c>).</param>
/// <param name="PromptVersion">A versão da proposta que o aluno usou; pode não ser mais a publicada.</param>
/// <param name="HasVersions">A redação é uma reescrita ou já foi reescrita.</param>
/// <param name="FailureCode">Código do erro quando o status é <c>failed</c> ou <c>cancelled</c>; o mesmo de <c>failure.code</c>.</param>
/// <param name="Failure">Motivo da falha ou do cancelamento; nulo nos outros status.</param>
public sealed record EssaySummaryDto(
    Guid Id,
    string Title,
    Guid PromptId,
    string PromptSlug,
    int PromptVersion,
    Guid EssayTypeId,
    EssayStatus Status,
    int Version,
    bool HasVersions,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReviewDeadlineAt,
    int IllegibleCount,
    int? Score,
    int? MaxScore,
    string? FailureCode,
    EssayFailureDto? Failure);

/// <summary>Por que a redação não foi corrigida. A tela escolhe o texto pelo <c>code</c>; a API não manda texto.</summary>
/// <param name="Code"><c>PHOTO_UNREADABLE</c>, <c>REVIEW_EXPIRED</c>, <c>TRANSCRIPTION_FAILED</c>, <c>GRADING_FAILED</c> ou <c>INTERNAL_ERROR</c>.</param>
/// <param name="OccurredAt">Quando a redação falhou ou foi cancelada.</param>
/// <param name="CreditRefunded">A correção reservada voltou para o saldo.</param>
public sealed record EssayFailureDto(string Code, DateTimeOffset OccurredAt, bool CreditRefunded, EssayEventDetailsDto? Details);

/// <summary>Dados para a tela; cada campo só aparece quando se aplica.</summary>
/// <param name="IllegibleCount">Palavras ilegíveis na leitura da foto.</param>
/// <param name="FailureCode">Código da falha, no evento <c>failed</c> ou <c>cancelled</c>.</param>
public sealed record EssayEventDetailsDto(int? IllegibleCount, string? FailureCode);

public enum EssayEventType
{
    Submitted,
    Transcribed,
    ReviewCompleted,
    GradingStarted,
    Completed,
    Failed,
    Cancelled,
    CreditRefunded,
}

public sealed record EssayEventDto(DateTimeOffset At, EssayEventType Type, EssayEventDetailsDto? Details);

/// <param name="NextCursor">Passe em <c>cursor</c> para a página seguinte; nulo na última.</param>
public sealed record EssayListDto(IReadOnlyList<EssaySummaryDto> Items, string? NextCursor);

public sealed record TranscriptionLineDto(int Number, string Text, bool Indent);

/// <param name="Start">Posição do trecho ilegível no texto da linha.</param>
/// <param name="ContextBefore">Trecho ao redor, para o aluno localizar a palavra.</param>
public sealed record IllegibleWordDto(
    Guid Id, int LineNumber, int Start, int End, string ContextBefore, string ContextAfter);

/// <param name="Timeline">O que aconteceu com a redação, do mais antigo para o mais novo.</param>
public sealed record EssayDetailDto(
    Guid Id,
    string Title,
    Guid PromptId,
    string PromptSlug,
    int PromptVersion,
    Guid EssayTypeId,
    EssayStatus Status,
    int Version,
    bool HasVersions,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReviewDeadlineAt,
    int IllegibleCount,
    int? Score,
    int? MaxScore,
    string? FailureCode,
    EssayFailureDto? Failure,
    int SheetLines,
    IReadOnlyList<TranscriptionLineDto> Lines,
    IReadOnlyList<IllegibleWordDto> IllegibleWords,
    IReadOnlyList<EssayEventDto> Timeline);
