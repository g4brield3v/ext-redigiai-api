using Microsoft.EntityFrameworkCore;
using RedigiAI.Domain.Catalog;

namespace RedigiAI.Infrastructure.Persistence.Repositories;

internal sealed class ExamRepository(AppDbContext db) : IExamRepository
{
    public async Task<IReadOnlyList<Exam>> ListAsync(CancellationToken ct) =>
        await db.Exams.AsNoTracking().OrderBy(e => e.Position).ToListAsync(ct);

    public Task<Exam?> FindBySlugAsync(string slug, CancellationToken ct) =>
        db.Exams.AsNoTracking().FirstOrDefaultAsync(e => e.Slug == slug, ct);

    public Task<Exam?> FindByIdAsync(Guid id, CancellationToken ct) =>
        db.Exams.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);

    public void Add(Exam exam) => db.Exams.Add(exam);
}

internal sealed class EssayTypeRepository(AppDbContext db) : IEssayTypeRepository
{
    public async Task<IReadOnlyList<EssayType>> ListByExamAsync(Guid examId, CancellationToken ct) =>
        await db.EssayTypes.AsNoTracking().Where(e => e.ExamId == examId).ToListAsync(ct);

    public void Add(EssayType essayType) => db.EssayTypes.Add(essayType);
}

internal sealed class RubricRepository(AppDbContext db) : IRubricRepository
{
    public Task<RubricVersion?> FindAsync(Guid rubricVersionId, CancellationToken ct) =>
        db.RubricVersions.AsNoTracking().Include(r => r.Criteria).FirstOrDefaultAsync(r => r.Id == rubricVersionId, ct);

    public Task<RubricVersion?> FindLatestAsync(Guid examId, CancellationToken ct) =>
        db.RubricVersions
            .AsNoTracking()
            .Include(r => r.Criteria)
            .Where(r => r.ExamId == examId)
            .OrderByDescending(r => r.Version)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, Guid>> LatestVersionIdsAsync(CancellationToken ct)
    {
        var versions = await db.RubricVersions
            .AsNoTracking()
            .Select(r => new { r.ExamId, r.Version, r.Id })
            .ToListAsync(ct);

        return versions
            .GroupBy(r => r.ExamId)
            .ToDictionary(g => g.Key, g => g.MaxBy(r => r.Version)!.Id);
    }

    public void Add(RubricVersion rubric) => db.RubricVersions.Add(rubric);
}

internal sealed class PromptRepository(AppDbContext db) : IPromptRepository
{
    public void Add(Prompt prompt) => db.Prompts.Add(prompt);

    public Task<Prompt?> FindForUpdateAsync(Guid promptId, CancellationToken ct) =>
        Whole(db.Prompts).FirstOrDefaultAsync(p => p.Id == promptId, ct);

    public Task<Prompt?> FindAsync(Guid promptId, CancellationToken ct) =>
        Whole(db.Prompts.AsNoTracking()).FirstOrDefaultAsync(p => p.Id == promptId, ct);

    public Task<Prompt?> FindPublishedAsync(string slug, CancellationToken ct) =>
        Whole(db.Prompts.AsNoTracking()).FirstOrDefaultAsync(p => p.Slug == slug && p.Status == PromptStatus.Published, ct);

    /// <summary>A proposta inteira, com os blocos na ordem de posição.</summary>
    private static IQueryable<Prompt> Whole(IQueryable<Prompt> prompts) =>
        prompts.Include(p => p.Blocks.OrderBy(b => b.Position)).Include(p => p.GradingNotes).AsSplitQuery();
}

internal sealed class ThemeRepository(AppDbContext db) : IThemeRepository
{
    public async Task<IReadOnlyList<Theme>> ListActiveAsync(Guid examId, string? titleSearch, CancellationToken ct)
    {
        var themes = Active().Where(t => t.ExamId == examId);
        if (!string.IsNullOrEmpty(titleSearch))
        {
            var pattern = $"%{EscapeLike(titleSearch)}%";
            themes = themes.Where(t => EF.Functions.ILike(t.Title, pattern, @"\"));
        }

        return await themes.OrderByDescending(t => t.PublishedAt).ThenBy(t => t.Title).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Theme>> ListForUpdateAsync(Guid examId, CancellationToken ct) =>
        await db.Themes.Include(t => t.AcceptedTypes).Where(t => t.ExamId == examId).ToListAsync(ct);

    public Task<Theme?> FindActiveAsync(Guid themeId, CancellationToken ct) =>
        Active().FirstOrDefaultAsync(t => t.Id == themeId, ct);

    public Task<Theme?> FindAsync(Guid themeId, CancellationToken ct) =>
        db.Themes
            .AsNoTracking()
            .Include(t => t.Texts)
            .Include(t => t.AcceptedTypes)
            .AsSplitQuery()
            .FirstOrDefaultAsync(t => t.Id == themeId, ct);

    public void Add(Theme theme) => db.Themes.Add(theme);

    private IQueryable<Theme> Active() =>
        db.Themes
            .AsNoTracking()
            .Include(t => t.Texts)
            .Include(t => t.AcceptedTypes)
            .AsSplitQuery()
            .Where(t => t.Status == ThemeStatus.Active);

    private static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
}
