using System.Text.Json.Serialization;
using RedigiAI.Domain.Catalog;

namespace RedigiAI.Application.Catalog;

/// <param name="CurrentRubricVersionId">Grade publicada mais recente; nula em vestibulares "em breve".</param>
public sealed record ExamDto(string Slug, string Name, ExamStatus Status, int SheetLines, Guid? CurrentRubricVersionId);

/// <summary>Categoria de correção: o nome é o tipo de redação que o aluno vê e o que entra no título da redação.</summary>
/// <param name="Slug"><c>dissertacao-em-prosa</c> ou <c>narrativa</c>.</param>
/// <param name="Name">"Dissertação em prosa".</param>
public sealed record EssayTypeDto(string Slug, string Name);

/// <summary>Uma proposta publicada na lista do vestibular.</summary>
/// <param name="EssayType">O tipo de redação da proposta.</param>
/// <param name="TextCount">Textos da coletânea.</param>
public sealed record PromptSummaryDto(
    Guid Id,
    string Slug,
    string Title,
    string? Summary,
    EssayTypeDto EssayType,
    int TextCount,
    string ExamSlug,
    string ExamName);

/// <summary>Um item de card para o aluno: texto, ou imagem com o endereço público. Sem a descrição feita para a IA.</summary>
public sealed record PromptCardItemPublicDto
{
    /// <summary><c>text</c> ou <c>image</c>.</summary>
    public string? Format { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Body { get; init; }

    /// <summary>Endereço público da imagem.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageUrl { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Alt { get; init; }

    /// <summary>Legenda ou fonte, abaixo do item.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Caption { get; init; }
}

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

    /// <summary>Itens das instruções. O primeiro, "Tipo de redação: …", é montado na tela com <c>essayType.name</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Items { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }

    /// <summary>Os 3 itens do card, empilhados na ordem.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<PromptCardItemPublicDto>? CardItems { get; init; }

    /// <summary>
    /// Texto da coletânea: <c>half</c> fica lado a lado com o texto vizinho que também é <c>half</c> (no celular, um
    /// embaixo do outro). Ausente: largura inteira.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Width { get; init; }

    /// <summary>Texto da coletânea: <c>true</c> quando o rótulo não aparece na tela.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? HideLabel { get; init; }

    /// <summary>Texto da coletânea: notas de rodapé, mostradas entre o texto e a fonte.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Notes { get; init; }
}

/// <param name="Kind">
/// <c>collection_item</c>, <c>card</c>, <c>command</c>, <c>instructions</c>, <c>heading</c>, <c>paragraph</c> ou <c>divider</c>.
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
    EssayTypeDto EssayType,
    string EssayTitle,
    string ExamSlug,
    string ExamName,
    int SheetLines,
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
