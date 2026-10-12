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

    public Task<Prompt?> FindReleasedAsync(string slug, int version, CancellationToken ct) =>
        Whole(db.Prompts.AsNoTracking())
            .FirstOrDefaultAsync(p => p.Slug == slug && p.Version == version && p.Status != PromptStatus.Draft, ct);

    public async Task<IReadOnlyList<PromptListItem>> ListPublishedAsync(Guid examId, string? titleSearch, CancellationToken ct) =>
        await Summaries(
            Search(db.Prompts.AsNoTracking().Where(p => p.ExamId == examId && p.Status == PromptStatus.Published), titleSearch)
                .OrderByDescending(p => p.PublishedAt)
                .ThenBy(p => p.Title))
            .ToListAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, PromptReference>> ReferencesAsync(
        IReadOnlyCollection<Guid> promptIds, CancellationToken ct)
    {
        if (promptIds.Count == 0)
        {
            return new Dictionary<Guid, PromptReference>();
        }

        return await db.Prompts
            .AsNoTracking()
            .Where(p => promptIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => new PromptReference(p.Slug, p.Version), ct);
    }

    public Task<bool> AnyAsync(Guid examId, CancellationToken ct) => db.Prompts.AnyAsync(p => p.ExamId == examId, ct);

    public Task LockAsync(Guid promptId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM prompts WHERE id = {promptId} FOR UPDATE", ct);

    public Task<Prompt?> FindByCreationKeyAsync(Guid adminId, string creationKey, CancellationToken ct) =>
        Whole(db.Prompts.AsNoTracking()).FirstOrDefaultAsync(p => p.CreatedBy == adminId && p.CreationKey == creationKey, ct);

    public async Task<IReadOnlyList<PromptListItem>> ListAsync(Guid? examId, string? titleSearch, CancellationToken ct)
    {
        var prompts = db.Prompts.AsNoTracking();
        if (examId is not null)
        {
            prompts = prompts.Where(p => p.ExamId == examId);
        }

        return await Summaries(
            Search(prompts, titleSearch).OrderByDescending(p => p.UpdatedAt).ThenBy(p => p.Slug).ThenByDescending(p => p.Version))
            .ToListAsync(ct);
    }

    private static IQueryable<Prompt> Search(IQueryable<Prompt> prompts, string? titleSearch)
    {
        if (string.IsNullOrEmpty(titleSearch))
        {
            return prompts;
        }

        var pattern = $"%{LikePattern.Escape(titleSearch)}%";
        return prompts.Where(p => EF.Functions.ILike(p.Title, pattern, @"\"));
    }

    private static IQueryable<PromptListItem> Summaries(IQueryable<Prompt> prompts) =>
        prompts.Select(p => new PromptListItem(
            p.Id,
            p.ExamId,
            p.Slug,
            p.Version,
            p.Status,
            p.Title,
            p.Summary,
            p.EssayTypeId,
            p.GenreLabel,
            p.Blocks.Count(b => b.Kind == PromptBlockKind.CollectionItem),
            p.UpdatedAt,
            p.SheetLinesMin,
            p.SheetLinesMax,
            p.TitleRequired));

    public async Task<int> MaxVersionAsync(string slug, CancellationToken ct) =>
        await db.Prompts.Where(p => p.Slug == slug).MaxAsync(p => (int?)p.Version, ct) ?? 0;

    public Task<bool> HasDraftAsync(string slug, CancellationToken ct) =>
        db.Prompts.AnyAsync(p => p.Slug == slug && p.Status == PromptStatus.Draft, ct);

    public Task<int> ArchivePublishedAsync(string slug, DateTimeOffset now, CancellationToken ct) =>
        db.Prompts
            .Where(p => p.Slug == slug && p.Status == PromptStatus.Published)
            .ExecuteUpdateAsync(p => p.SetProperty(x => x.Status, PromptStatus.Archived).SetProperty(x => x.UpdatedAt, now), ct);

    public async Task<bool> TryPublishAsync(Guid promptId, Guid? publishedBy, DateTimeOffset now, CancellationToken ct) =>
        await db.Prompts
            .Where(p => p.Id == promptId && p.Status == PromptStatus.Draft)
            .ExecuteUpdateAsync(
                p => p
                    .SetProperty(x => x.Status, PromptStatus.Published)
                    .SetProperty(x => x.PublishedAt, now)
                    .SetProperty(x => x.PublishedBy, publishedBy)
                    .SetProperty(x => x.UpdatedAt, now),
                ct) == 1;

    public async Task<bool> TryArchiveAsync(Guid promptId, DateTimeOffset now, CancellationToken ct) =>
        await db.Prompts
            .Where(p => p.Id == promptId && p.Status == PromptStatus.Published)
            .ExecuteUpdateAsync(p => p.SetProperty(x => x.Status, PromptStatus.Archived).SetProperty(x => x.UpdatedAt, now), ct) == 1;

    public async Task<bool> TryDeleteDraftAsync(Guid promptId, CancellationToken ct) =>
        await db.Prompts.Where(p => p.Id == promptId && p.Status == PromptStatus.Draft).ExecuteDeleteAsync(ct) == 1;

    /// <summary>A proposta inteira, com os blocos na ordem de posição.</summary>
    private static IQueryable<Prompt> Whole(IQueryable<Prompt> prompts) =>
        prompts.Include(p => p.Blocks.OrderBy(b => b.Position)).Include(p => p.GradingNotes).AsSplitQuery();
}

internal static class LikePattern
{
    /// <summary>O texto procurado vale literalmente: <c>%</c> e <c>_</c> não são curingas.</summary>
    public static string Escape(string value) =>
        value.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
}
