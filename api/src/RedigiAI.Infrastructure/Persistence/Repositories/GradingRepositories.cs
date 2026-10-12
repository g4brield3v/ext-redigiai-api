using Microsoft.EntityFrameworkCore;
using RedigiAI.Domain.Grading;

namespace RedigiAI.Infrastructure.Persistence.Repositories;

internal sealed class GradingProfileRepository(AppDbContext db) : IGradingProfileRepository
{
    public void Add(GradingProfile profile) => db.GradingProfiles.Add(profile);

    public Task<GradingProfile?> FindActiveAsync(Guid examId, Guid essayTypeId, CancellationToken ct) =>
        db.GradingProfiles.AsNoTracking().FirstOrDefaultAsync(
            p => p.ExamId == examId && p.EssayTypeId == essayTypeId && p.Status == GradingProfileStatus.Active, ct);
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

internal sealed class ThemeGradingNotesRepository(AppDbContext db) : IThemeGradingNotesRepository
{
    public void Add(ThemeGradingNotes notes) => db.ThemeGradingNotes.Add(notes);

    public Task<ThemeGradingNotes?> FindLatestAsync(Guid themeId, CancellationToken ct) =>
        db.ThemeGradingNotes.AsNoTracking().Where(n => n.ThemeId == themeId).OrderByDescending(n => n.Version).FirstOrDefaultAsync(ct);
}
