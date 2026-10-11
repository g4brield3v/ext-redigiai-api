using RedigiAI.Domain.Catalog;

namespace RedigiAI.Application.Catalog;

/// <summary>Leituras do catálogo público, direto do banco, sem carregar agregados.</summary>
public interface ICatalogQueries
{
    Task<IReadOnlyList<ExamDto>> ListExamsAsync(CancellationToken ct);

    /// <returns><c>null</c> se o vestibular não existe ou não está ativo.</returns>
    Task<IReadOnlyList<ThemeSummaryDto>?> ListThemesAsync(string examSlug, string? search, CancellationToken ct);

    /// <returns><c>null</c> se o tema não existe ou não está ativo.</returns>
    Task<ThemeDetailDto?> GetThemeAsync(Guid themeId, CancellationToken ct);

    Task<RubricDto?> GetRubricAsync(Guid rubricVersionId, CancellationToken ct);
}

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
    int MaxScore,
    IReadOnlyList<RubricCriterionDto> Criteria,
    IReadOnlyList<ZeroRuleDto> ZeroRules);
