using System.Text;
using RedigiAI.Domain.Catalog;

namespace RedigiAI.Application.Catalog;

/// <summary>Catálogo público: vestibulares, temas e grades de correção.</summary>
public interface ICatalogService
{
    Task<IReadOnlyList<ExamDto>> ListExamsAsync(CancellationToken ct);

    /// <returns><c>null</c> se o vestibular não existe ou não está ativo.</returns>
    Task<IReadOnlyList<ThemeSummaryDto>?> ListThemesAsync(string examSlug, string? search, CancellationToken ct);

    /// <returns><c>null</c> se o tema não existe ou não está ativo.</returns>
    Task<ThemeDetailDto?> GetThemeAsync(Guid themeId, CancellationToken ct);

    Task<RubricDto?> GetRubricAsync(Guid rubricVersionId, CancellationToken ct);
}

internal sealed class CatalogService(
    IExamRepository exams,
    IEssayTypeRepository essayTypes,
    IThemeRepository themes,
    IRubricRepository rubrics) : ICatalogService
{
    private const int MaxSearchLength = 80;

    public async Task<IReadOnlyList<ExamDto>> ListExamsAsync(CancellationToken ct)
    {
        var latestRubrics = await rubrics.LatestVersionIdsAsync(ct);

        return [.. (await exams.ListAsync(ct)).Select(e => new ExamDto(
            e.Slug, e.Name, e.Status, e.SheetLines, latestRubrics.TryGetValue(e.Id, out var rubricId) ? rubricId : null))];
    }

    public async Task<IReadOnlyList<ThemeSummaryDto>?> ListThemesAsync(string examSlug, string? search, CancellationToken ct)
    {
        var exam = await exams.FindBySlugAsync(examSlug, ct);
        if (exam is not { Status: ExamStatus.Active })
        {
            return null;
        }

        var types = await ListTypesAsync(exam.Id, ct);
        var found = await themes.ListActiveAsync(exam.Id, CleanSearch(search), ct);

        return [.. found.Select(t => new ThemeSummaryDto(
            t.Id, t.Title, exam.Slug, exam.Name, ToDtos(t.AcceptedTypes, types), t.Texts.Count))];
    }

    public async Task<ThemeDetailDto?> GetThemeAsync(Guid themeId, CancellationToken ct)
    {
        var theme = await themes.FindActiveAsync(themeId, ct);
        if (theme is null)
        {
            return null;
        }

        var exam = await exams.FindByIdAsync(theme.ExamId, ct);
        if (exam is not { Status: ExamStatus.Active })
        {
            return null;
        }

        return new ThemeDetailDto(
            theme.Id,
            theme.Title,
            theme.Statement,
            exam.Slug,
            exam.Name,
            exam.SheetLines,
            [.. theme.Texts.OrderBy(t => t.Position).Select(t => new ThemeTextDto(t.Position, t.Kind, t.Title, t.Source, t.Body))],
            ToDtos(theme.AcceptedTypes, await ListTypesAsync(exam.Id, ct)));
    }

    public async Task<RubricDto?> GetRubricAsync(Guid rubricVersionId, CancellationToken ct)
    {
        var rubric = await rubrics.FindAsync(rubricVersionId, ct);
        if (rubric is null || await exams.FindByIdAsync(rubric.ExamId, ct) is not { } exam)
        {
            return null;
        }

        return new RubricDto(
            rubric.Id,
            exam.Slug,
            exam.Name,
            rubric.Version,
            rubric.MaxScore,
            [.. rubric.Criteria.OrderBy(c => c.Position).Select(c => new RubricCriterionDto(
                c.Position,
                c.Name,
                c.Weight,
                c.MaxPoints,
                [.. c.LevelDescriptors.OrderBy(l => l.Key).Select(l => new RubricLevelDto(l.Key, l.Value))]))],
            [.. rubric.ZeroRules.Select(z => new ZeroRuleDto(z.Code, z.Description))]);
    }

    private async Task<Dictionary<Guid, EssayType>> ListTypesAsync(Guid examId, CancellationToken ct) =>
        (await essayTypes.ListByExamAsync(examId, ct)).ToDictionary(t => t.Id);

    private static IReadOnlyList<EssayTypeDto> ToDtos(
        IEnumerable<ThemeEssayType> accepted, Dictionary<Guid, EssayType> types) =>
        [.. accepted
            .Select(a => (Accepted: a, Type: types[a.EssayTypeId]))
            .Select(x => new EssayTypeDto(
                x.Type.Id, x.Type.Slug, x.Type.Name, x.Type.Description, x.Accepted.RubricVersionId, x.Accepted.Instruction))
            .OrderBy(e => e.Name)];

    /// <summary>Tira caracteres de controle (o PostgreSQL recusa NUL) e corta sem partir um par substituto.</summary>
    private static string? CleanSearch(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return null;
        }

        var clean = new StringBuilder(MaxSearchLength);
        foreach (var rune in search.EnumerateRunes())
        {
            if (clean.Length + rune.Utf16SequenceLength > MaxSearchLength)
            {
                break;
            }

            if (!Rune.IsControl(rune) && rune != Rune.ReplacementChar)
            {
                clean.Append(rune.ToString());
            }
        }

        var trimmed = clean.ToString().Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
