using RedigiAI.Domain.Catalog;

namespace RedigiAI.Application.Catalog;

/// <param name="CurrentRubricVersionId">Grade publicada mais recente; nula em vestibulares "em breve".</param>
public sealed record ExamDto(string Slug, string Name, ExamStatus Status, int SheetLines, Guid? CurrentRubricVersionId);

/// <param name="Instruction">O que a proposta do tema pede para esse tipo de texto.</param>
public sealed record EssayTypeDto(
    Guid Id, string Slug, string Name, string Description, Guid RubricVersionId, string Instruction);

public sealed record ThemeSummaryDto(
    Guid Id, string Title, string ExamSlug, string ExamName, IReadOnlyList<EssayTypeDto> EssayTypes, int TextCount);

public sealed record ThemeTextDto(int Position, ThemeTextKind Kind, string Title, string Source, string? Body);

public sealed record ThemeDetailDto(
    Guid Id,
    string Title,
    string Statement,
    string ExamSlug,
    string ExamName,
    int SheetLines,
    IReadOnlyList<ThemeTextDto> Texts,
    IReadOnlyList<EssayTypeDto> EssayTypes);

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
