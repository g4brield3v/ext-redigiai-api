using System.Text.Json.Serialization;
using RedigiAI.Domain.Catalog;

namespace RedigiAI.Application.Catalog;

/// <summary>
/// Proposta no formato de cadastro (docs/07-propostas-de-redacao.md, B4): é o corpo da criação, da importação e do
/// salvamento, e o que "Exportar JSON" entrega. Tudo é opcional: um rascunho aceita conteúdo incompleto.
/// </summary>
/// <param name="Exam">Endereço do vestibular (<c>fuvest</c>).</param>
/// <param name="EssayType">Categoria de correção (<c>dissertacao</c> ou <c>narrativa</c>).</param>
/// <param name="ExpectedUpdatedAt">
/// Só no salvamento: o <c>updatedAt</c> que o editor tem. Se a proposta mudou depois disso, <c>PROMPT_CHANGED</c>.
/// </param>
public sealed record PromptDocument(
    string? Exam = null,
    string? Slug = null,
    string? Title = null,
    string? Summary = null,
    string? SourceLabel = null,
    string? EssayType = null,
    string? GenreLabel = null,
    PromptSheetDto? Sheet = null,
    IReadOnlyList<PromptBlockDto>? Blocks = null,
    PromptNotesDto? GradingNotes = null,
    DateTimeOffset? ExpectedUpdatedAt = null);

public sealed record PromptSheetDto(int? MinLines = null, int? MaxLines = null, bool? TitleRequired = null);

/// <param name="Id">Na resposta, o id do bloco; no salvamento, identifica o bloco que já existe.</param>
/// <param name="Kind">
/// <c>collection_item</c>, <c>command</c>, <c>instructions</c>, <c>heading</c>, <c>paragraph</c> ou <c>divider</c>.
/// </param>
public sealed record PromptBlockDto(Guid? Id = null, int? Position = null, string? Kind = null, PromptBlockDataDto? Data = null);

/// <summary>Conteúdo de um bloco; cada tipo usa só os seus campos.</summary>
public sealed record PromptBlockDataDto
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; init; }

    /// <summary><c>prose</c>, <c>verse</c> ou <c>image</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Format { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Body { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<IReadOnlyList<string>>? Columns { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? AssetId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Alt { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AiDescription { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Items { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }

    /// <summary>Só na resposta: endereço público da imagem do bloco. Ignorado quando enviado.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageUrl { get; init; }
}

public sealed record PromptNotesDto(
    string? ExpectedFocus = null,
    string? TangentSignals = null,
    string? OffTopicSignals = null,
    IReadOnlyList<string>? KeyData = null,
    string? Extra = null);

/// <summary>Uma pendência: o caminho do campo no JSON da proposta e a mensagem para quem cadastra.</summary>
public sealed record PromptIssueDto(string Path, string Message);

/// <summary>A proposta inteira, para o editor e para "Exportar JSON" (o mesmo formato da importação, mais os campos de controle).</summary>
public sealed record PromptAdminDto(
    Guid Id,
    string Exam,
    string Slug,
    int Version,
    PromptStatus Status,
    string Title,
    string? Summary,
    string? SourceLabel,
    string? EssayType,
    string? GenreLabel,
    PromptSheetDto Sheet,
    IReadOnlyList<PromptBlockDto> Blocks,
    PromptNotesDto GradingNotes,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<PromptIssueDto> Issues);

public sealed record SavedBlockDto(Guid Id, int Position);

/// <summary>Resposta de toda escrita: a situação da versão, os blocos com os seus ids e o que falta para publicar.</summary>
public sealed record PromptSavedDto(
    Guid Id,
    string Slug,
    int Version,
    PromptStatus Status,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<SavedBlockDto> Blocks,
    IReadOnlyList<PromptIssueDto> Issues);

public sealed record PromptValidationDto(bool Valid, IReadOnlyList<PromptIssueDto> Issues);

/// <param name="EssayTypeName">Nome da categoria de correção ("Dissertação").</param>
/// <param name="TextCount">Textos da coletânea.</param>
public sealed record PromptListItemDto(
    Guid Id,
    string Exam,
    string ExamName,
    string Slug,
    int Version,
    PromptStatus Status,
    string Title,
    string? GenreLabel,
    string? EssayTypeName,
    int TextCount,
    DateTimeOffset UpdatedAt);

/// <summary>Quantas versões há em cada situação, com a busca e o vestibular aplicados e sem o filtro de situação.</summary>
public sealed record PromptCountsDto(int All, int Draft, int Published, int Archived);

public sealed record PromptListDto(IReadOnlyList<PromptListItemDto> Items, PromptCountsDto Counts);

public sealed record PromptImageDto(Guid AssetId, string Url, int Width, int Height);

/// <param name="ContentType">O que o navegador declarou; o tipo que vale é o do conteúdo do arquivo.</param>
public sealed record PromptImageUpload(byte[] Content, string? ContentType);

public sealed record CreatedPrompt(PromptSavedDto Prompt, bool Created);
