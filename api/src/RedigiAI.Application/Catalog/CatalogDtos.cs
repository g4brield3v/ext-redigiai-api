using System.Text.Json.Serialization;
using RedigiAI.Domain.Catalog;

namespace RedigiAI.Application.Catalog;

/// <param name="CurrentRubricVersionId">Grade publicada mais recente; nula em vestibulares "em breve".</param>
public sealed record ExamDto(string Slug, string Name, ExamStatus Status, int SheetLines, Guid? CurrentRubricVersionId);

/// <summary>Uma proposta publicada na lista do vestibular.</summary>
/// <param name="GenreLabel">O tipo de redação exatamente como a prova pede ("Dissertação em prosa").</param>
/// <param name="EssayTypeName">A categoria de correção ("Dissertação"), que entra no título da redação.</param>
/// <param name="TextCount">Textos da coletânea.</param>
public sealed record PromptSummaryDto(
    Guid Id,
    string Slug,
    string Title,
    string? Summary,
    string GenreLabel,
    string EssayTypeName,
    int TextCount,
    int? MinLines,
    int? MaxLines,
    bool TitleRequired,
    string ExamSlug,
    string ExamName);

/// <summary>Conteúdo de um bloco para o aluno; cada tipo usa só os seus campos. Os textos trazem o markdown mínimo.</summary>
public sealed record PromptBlockPublicDataDto
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

    /// <summary>Endereço público da imagem.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageUrl { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Alt { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    /// <summary>Itens das instruções. O primeiro, "Tipo de redação: …", é montado na tela com <c>genreLabel</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Items { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }
}

/// <param name="Kind">
/// <c>collection_item</c>, <c>command</c>, <c>instructions</c>, <c>heading</c>, <c>paragraph</c> ou <c>divider</c>.
/// </param>
public sealed record PromptBlockPublicDto(int Position, string Kind, PromptBlockPublicDataDto Data);

/// <summary>A proposta como o aluno lê, com os blocos na ordem da página. Não traz as abordagens esperadas.</summary>
/// <param name="Status"><c>published</c>, ou <c>archived</c> quando é a versão antiga que uma redação usou.</param>
/// <param name="EssayTitle">O título que a redação vai ter: "Vestibular – Tipo – Tema".</param>
/// <param name="SheetLines">Linhas da folha do vestibular.</param>
public sealed record PromptDetailDto(
    Guid Id,
    string Slug,
    int Version,
    PromptStatus Status,
    string Title,
    string? Summary,
    string GenreLabel,
    Guid EssayTypeId,
    string EssayTypeName,
    string EssayTitle,
    string ExamSlug,
    string ExamName,
    int SheetLines,
    int? MinLines,
    int? MaxLines,
    bool TitleRequired,
    int TextCount,
    IReadOnlyList<PromptBlockPublicDto> Blocks);

public sealed record RubricLevelDto(int Level, string Descriptor);

public sealed record RubricCriterionDto(
    int Position, string Name, int Weight, int MaxPoints, IReadOnlyList<RubricLevelDto> Levels);

public sealed record ZeroRuleDto(string Code, string Description);

public sealed record RubricDto(
    Guid Id,
    string ExamSlug,
    string ExamName,
    int Version,
    int MaxLevel,
    int MaxScore,
    IReadOnlyList<RubricCriterionDto> Criteria,
    IReadOnlyList<ZeroRuleDto> ZeroRules);
