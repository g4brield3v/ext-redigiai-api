using Microsoft.EntityFrameworkCore;
using RedigiAI.Domain.Grading;

namespace RedigiAI.Infrastructure.Persistence.Repositories;

internal sealed class GradingProfileRepository(AppDbContext db) : IGradingProfileRepository
{
    public void Add(GradingProfile profile) => db.GradingProfiles.Add(profile);

    public Task<GradingProfile?> FindActiveAsync(Guid examId, Guid essayTypeId, CancellationToken ct) =>
        db.GradingProfiles.AsNoTracking().FirstOrDefaultAsync(
            p => p.ExamId == examId && p.EssayTypeId == essayTypeId && p.Status == GradingProfileStatus.Active, ct);

    public Task<GradingProfile?> FindAsync(Guid id, CancellationToken ct) =>
        db.GradingProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<bool> TryRetireAsync(Guid profileId, CancellationToken ct) =>
        await db.GradingProfiles
            .Where(p => p.Id == profileId && p.Status == GradingProfileStatus.Active)
            .ExecuteUpdateAsync(p => p.SetProperty(x => x.Status, GradingProfileStatus.Retired), ct) == 1;

    public async Task<int> MaxVersionAsync(Guid examId, Guid essayTypeId, CancellationToken ct) =>
        await db.GradingProfiles
            .Where(p => p.ExamId == examId && p.EssayTypeId == essayTypeId)
            .MaxAsync(p => (int?)p.Version, ct) ?? 0;
}

internal sealed class PromptTemplateRepository(AppDbContext db) : IPromptTemplateRepository
{
    public void Add(PromptTemplate template) => db.PromptTemplates.Add(template);

    public Task<PromptTemplate?> FindAsync(Guid id, CancellationToken ct) =>
        db.PromptTemplates.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);

    public Task<PromptTemplate?> FindLatestAsync(PromptKind kind, CancellationToken ct) =>
        db.PromptTemplates.AsNoTracking().Where(p => p.Kind == kind).OrderByDescending(p => p.Version).FirstOrDefaultAsync(ct);
}

internal sealed class GuidelinesRepository(AppDbContext db) : IGuidelinesRepository
{
    public void Add(GuidelinesVersion guidelines) => db.GuidelinesVersions.Add(guidelines);

    public Task<GuidelinesVersion?> FindAsync(Guid id, CancellationToken ct) =>
        db.GuidelinesVersions.AsNoTracking().Include(g => g.Criteria).FirstOrDefaultAsync(g => g.Id == id, ct);

    public Task<GuidelinesVersion?> FindLatestAsync(Guid examId, CancellationToken ct) =>
        db.GuidelinesVersions.AsNoTracking().Where(g => g.ExamId == examId).OrderByDescending(g => g.Version).FirstOrDefaultAsync(ct);
}

internal sealed class WritingRulesRepository(AppDbContext db) : IWritingRulesRepository
{
    public void Add(WritingRulesVersion rules) => db.WritingRulesVersions.Add(rules);

    public Task<WritingRulesVersion?> FindAsync(Guid id, CancellationToken ct) =>
        db.WritingRulesVersions.AsNoTracking().Include(v => v.Rules).FirstOrDefaultAsync(v => v.Id == id, ct);

    public Task<WritingRulesVersion?> FindLatestAsync(CancellationToken ct) =>
        db.WritingRulesVersions.AsNoTracking().OrderByDescending(v => v.Version).FirstOrDefaultAsync(ct);
}

internal sealed class CorrectionRepository(AppDbContext db) : ICorrectionRepository
{
    public void Add(Correction correction) => db.Corrections.Add(correction);

    public Task<bool> ExistsAsync(Guid essayId, CancellationToken ct) =>
        db.Corrections.AsNoTracking().AnyAsync(c => c.EssayId == essayId, ct);

    public Task<Correction?> FindByEssayAsync(Guid essayId, CancellationToken ct) =>
        db.Corrections
            .AsNoTracking()
            .Include(c => c.Criteria)
            .Include(c => c.Marks)
            .AsSplitQuery()
            .FirstOrDefaultAsync(c => c.EssayId == essayId, ct);

    public Task<Correction?> FindAsync(Guid id, CancellationToken ct) =>
        db.Corrections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);

    public void AddFeedback(CorrectionFeedback feedback) => db.CorrectionFeedback.Add(feedback);

    public Task<CorrectionFeedback?> FindFeedbackAsync(Guid correctionId, CancellationToken ct) =>
        db.CorrectionFeedback.AsNoTracking().FirstOrDefaultAsync(f => f.CorrectionId == correctionId, ct);

    public async Task<IReadOnlyDictionary<Guid, (int Score, int MaxScore)>> ScoresAsync(
        IReadOnlyCollection<Guid> essayIds, CancellationToken ct)
    {
        if (essayIds.Count == 0)
        {
            return new Dictionary<Guid, (int, int)>();
        }

        var scores = await db.Corrections
            .AsNoTracking()
            .Where(c => essayIds.Contains(c.EssayId))
            .Select(c => new { c.EssayId, c.Score, c.MaxScore })
            .ToListAsync(ct);

        return scores.ToDictionary(c => c.EssayId, c => (c.Score, c.MaxScore));
    }
}

