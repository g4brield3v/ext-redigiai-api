using Microsoft.EntityFrameworkCore;
using RedigiAI.Application.Catalog;
using RedigiAI.Domain.Catalog;

namespace RedigiAI.Infrastructure.Persistence;

internal sealed class CatalogQueries(AppDbContext db) : ICatalogQueries
{
    public async Task<IReadOnlyList<ExamDto>> ListExamsAsync(CancellationToken ct) =>
        await db.Exams
            .AsNoTracking()
            .OrderBy(e => e.Position)
            .Select(e => new ExamDto(
                e.Slug,
                e.Name,
                e.Status,
                e.SheetLines,
                db.RubricVersions
                    .Where(r => r.ExamId == e.Id)
                    .OrderByDescending(r => r.Version)
                    .Select(r => (Guid?)r.Id)
                    .FirstOrDefault()))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<ThemeSummaryDto>?> ListThemesAsync(
        string examSlug, string? search, CancellationToken ct)
    {
        var exam = await FindActiveExamAsync(e => e.Slug == examSlug, ct);
        if (exam is null)
        {
            return null;
        }

        var themes = db.Themes.AsNoTracking().Where(t => t.ExamId == exam.Id && t.Status == ThemeStatus.Active);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{EscapeLike(search.Trim())}%";
            themes = themes.Where(t => EF.Functions.ILike(t.Title, pattern, @"\"));
        }

        var rows = await themes
            .OrderByDescending(t => t.PublishedAt)
            .ThenBy(t => t.Title)
            .Select(t => new
            {
                t.Id,
                t.Title,
                TextCount = t.Texts.Count,
                Accepted = t.AcceptedTypes.Select(a => new { a.EssayTypeId, a.RubricVersionId }).ToList(),
            })
            .ToListAsync(ct);

        var types = await db.EssayTypes.AsNoTracking().Where(e => e.ExamId == exam.Id).ToDictionaryAsync(e => e.Id, ct);

        return rows
            .Select(t => new ThemeSummaryDto(
                t.Id,
                t.Title,
                exam.Slug,
                exam.Name,
                [.. t.Accepted.Select(a => ToDto(types[a.EssayTypeId], a.RubricVersionId)).OrderBy(e => e.Name)],
                t.TextCount))
            .ToList();
    }

    public async Task<ThemeDetailDto?> GetThemeAsync(Guid themeId, CancellationToken ct)
    {
        var theme = await db.Themes
            .AsNoTracking()
            .Include(t => t.Texts)
            .Include(t => t.AcceptedTypes)
            .AsSplitQuery()
            .FirstOrDefaultAsync(t => t.Id == themeId && t.Status == ThemeStatus.Active, ct);
        if (theme is null)
        {
            return null;
        }

        var exam = await FindActiveExamAsync(e => e.Id == theme.ExamId, ct);
        if (exam is null)
        {
            return null;
        }

        var typeIds = theme.AcceptedTypes.Select(a => a.EssayTypeId).ToList();
        var types = await db.EssayTypes.AsNoTracking().Where(e => typeIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);

        return new ThemeDetailDto(
            theme.Id,
            theme.Title,
            theme.Statement,
            exam.Slug,
            exam.Name,
            exam.SheetLines,
            [.. theme.Texts.OrderBy(t => t.Position).Select(t => new ThemeTextDto(t.Position, t.Kind, t.Title, t.Source, t.Body))],
            [.. theme.AcceptedTypes.Select(a => ToDto(types[a.EssayTypeId], a.RubricVersionId)).OrderBy(e => e.Name)]);
    }

    public async Task<RubricDto?> GetRubricAsync(Guid rubricVersionId, CancellationToken ct)
    {
        var rubric = await db.RubricVersions
            .AsNoTracking()
            .Include(r => r.Criteria)
            .FirstOrDefaultAsync(r => r.Id == rubricVersionId, ct);
        if (rubric is null)
        {
            return null;
        }

        var exam = await db.Exams.AsNoTracking().FirstAsync(e => e.Id == rubric.ExamId, ct);

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

    private Task<Exam?> FindActiveExamAsync(
        System.Linq.Expressions.Expression<Func<Exam, bool>> predicate, CancellationToken ct) =>
        db.Exams.AsNoTracking().Where(e => e.Status == ExamStatus.Active).FirstOrDefaultAsync(predicate, ct);

    private static EssayTypeDto ToDto(EssayType type, Guid rubricVersionId) =>
        new(type.Id, type.Slug, type.Name, type.Description, rubricVersionId);

    private static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
}
