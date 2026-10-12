using System.Globalization;
using RedigiAI.Application.Essays;
using RedigiAI.Domain.Accounts;
using RedigiAI.Domain.Catalog;
using RedigiAI.Domain.Common;
using RedigiAI.Domain.Essays;
using RedigiAI.Domain.Grading;

namespace RedigiAI.Application.Grading;

/// <param name="Line">Linha da folha onde o trecho está.</param>
/// <param name="Start">Posição no texto da linha: início incluído, fim excluído.</param>
/// <param name="Note">Comentário da margem, exibido na mesma linha da marca.</param>
/// <param name="Tagged">A marca leva o selo do critério ao lado do comentário.</param>
public sealed record CorrectionMarkDto(int Criterion, MarkKind Kind, int Line, int Start, int End, string? Note, bool Tagged);

/// <param name="Rule">Nome da regra de escrita, como "Crase antes de verbo".</param>
/// <param name="Correction">O que corrigir nesse trecho.</param>
public sealed record RuleDeviationDto(string Rule, int Line, string Correction);

/// <param name="Points">Nível × peso, calculado pelo backend.</param>
/// <param name="Descriptor">Descritor oficial do nível, da grade usada.</param>
/// <param name="LinesLabel">Referência pronta às linhas, como "Linhas 3 a 7".</param>
/// <param name="Guidance">O que corrigir, sempre como afirmação; nulo quando o nível é o máximo.</param>
/// <param name="Deviations">Desvios de escrita agrupados pela regra.</param>
public sealed record CorrectionCriterionDto(
    int Position,
    string Name,
    int Weight,
    int Level,
    int MaxLevel,
    int Points,
    int MaxPoints,
    string Descriptor,
    string Justification,
    IReadOnlyList<int> Lines,
    string LinesLabel,
    string? Guidance,
    IReadOnlyList<RuleDeviationDto> Deviations);

/// <param name="Title">"Vestibular – Tipo – Tema".</param>
/// <param name="Version">Versão da redação: 1 no primeiro envio, 2 na primeira reescrita.</param>
/// <param name="Score">Nota da grade, sempre calculada pelo backend.</param>
/// <param name="ScoreDelta">Diferença para a nota da versão anterior da mesma redação, se ela foi corrigida.</param>
/// <param name="ZeroReason">Código da hipótese de nota zero aplicada; nulo quando os critérios foram pontuados.</param>
/// <param name="ZeroRule">Texto da hipótese de nota zero, como está na grade.</param>
/// <param name="Lines">A redação como foi corrigida: a transcrição com as palavras digitadas pelo aluno.</param>
public sealed record CorrectionDto(
    Guid Id,
    Guid EssayId,
    string Title,
    Guid ThemeId,
    Guid EssayTypeId,
    DateTimeOffset SentAt,
    int Version,
    string ExamName,
    int RubricVersion,
    int Score,
    int MaxScore,
    int? ScoreDelta,
    int? PreviousVersion,
    string? ZeroReason,
    string? ZeroRule,
    string? ZeroExplanation,
    string Strengths,
    string MainIssue,
    int SheetLines,
    IReadOnlyList<TranscriptionLineDto> Lines,
    IReadOnlyList<CorrectionMarkDto> Marks,
    IReadOnlyList<CorrectionCriterionDto> Criteria,
    IReadOnlyList<string> NextSteps);

public interface ICorrectionService
{
    /// <summary>A correção completa de uma redação do aluno.</summary>
    /// <exception cref="AppError">
    /// <c>ESSAY_NOT_FOUND</c> se a redação não é do aluno; <c>INVALID_STATE</c> se ainda não foi corrigida.
    /// </exception>
    Task<CorrectionDto> GetAsync(string clerkUserId, Guid essayId, CancellationToken ct);
}

internal sealed class CorrectionService(
    IUserRepository users,
    IEssayRepository essays,
    IExamRepository exams,
    IRubricRepository rubrics,
    IGradingProfileRepository profiles,
    IWritingRulesRepository writingRules,
    ITranscriptionRepository transcriptions,
    ICorrectionRepository corrections) : ICorrectionService
{
    public async Task<CorrectionDto> GetAsync(string clerkUserId, Guid essayId, CancellationToken ct)
    {
        var user = await users.FindByClerkIdAsync(clerkUserId, ct) ?? throw Errors.ProfileNotFound();
        var essay = await essays.FindAsync(essayId, user.Id, ct) ?? throw Errors.EssayNotFound();
        var correction = await corrections.FindByEssayAsync(essay.Id, ct) ?? throw Errors.InvalidState();

        var exam = await exams.FindByIdAsync(essay.ExamId, ct) ?? throw Errors.Internal();
        var rubric = await rubrics.FindAsync(correction.RubricVersionId, ct) ?? throw Errors.Internal();
        var profile = await profiles.FindAsync(correction.GradingProfileId, ct) ?? throw Errors.Internal();
        var rules = await writingRules.FindAsync(profile.WritingRulesVersionId, ct);
        var transcription = await transcriptions.FindByEssayAsync(essay.Id, ct) ?? throw Errors.Internal();
        var (previousVersion, scoreDelta) = await ComparedToPreviousAsync(essay, user.Id, correction.Score, ct);

        var ruleNames = (rules?.Rules ?? []).ToDictionary(rule => rule.Code, rule => rule.Name);
        var marks = correction.Marks.OrderBy(mark => mark.LineStart).ThenBy(mark => mark.CharStart).ToList();

        return new CorrectionDto(
            correction.Id,
            essay.Id,
            essay.Title,
            essay.ThemeId,
            essay.EssayTypeId,
            essay.CreatedAt,
            essay.Version,
            exam.Name,
            rubric.Version,
            correction.Score,
            correction.MaxScore,
            scoreDelta,
            previousVersion,
            correction.ZeroReason,
            rubric.ZeroRules.FirstOrDefault(rule => rule.Code == correction.ZeroReason)?.Description,
            correction.ZeroExplanation,
            correction.Strengths,
            correction.MainIssue,
            exam.SheetLines,
            [.. transcription.FinalLines().Select(line => new TranscriptionLineDto(line.Number, line.Text, line.Indent))],
            [.. marks.Select(mark => new CorrectionMarkDto(
                mark.CriterionPosition, mark.Kind, mark.LineStart, mark.CharStart, mark.CharEnd, mark.Note, mark.Note is not null))],
            [.. correction.Criteria.OrderBy(c => c.Position).Select(graded => ToDto(graded, rubric, marks, ruleNames))],
            correction.NextSteps);
    }

    /// <summary>Uma reescrita mostra quanto a nota mudou desde a versão anterior, se ela chegou a ser corrigida.</summary>
    private async Task<(int? PreviousVersion, int? ScoreDelta)> ComparedToPreviousAsync(
        Essay essay, Guid userId, int score, CancellationToken ct)
    {
        if (essay.ParentEssayId is not { } parentId
            || await essays.FindAsync(parentId, userId, ct) is not { } parent
            || !(await corrections.ScoresAsync([parent.Id], ct)).TryGetValue(parent.Id, out var previous))
        {
            return (null, null);
        }

        return (parent.Version, score - previous.Score);
    }

    private static CorrectionCriterionDto ToDto(
        CorrectionCriterion graded,
        RubricVersion rubric,
        List<CorrectionMark> marks,
        Dictionary<string, string> ruleNames)
    {
        var criterion = rubric.Criteria.Single(c => c.Id == graded.CriterionId);
        return new CorrectionCriterionDto(
            graded.Position,
            criterion.Name,
            criterion.Weight,
            graded.Level,
            rubric.MaxLevel,
            graded.Points,
            criterion.MaxPoints,
            criterion.LevelDescriptors[graded.Level],
            graded.Justification,
            graded.Lines,
            LinesLabel(graded.Lines),
            graded.Guidance,
            [.. marks
                .Where(mark => mark.CriterionId == criterion.Id && mark is { Kind: MarkKind.Issue, RuleCode: not null, Note: not null })
                .Select(mark => new RuleDeviationDto(ruleNames.GetValueOrDefault(mark.RuleCode!, mark.RuleCode!), mark.LineStart, mark.Note!))
                // Agrupados pela regra, na ordem em que cada regra aparece na redação.
                .GroupBy(deviation => deviation.Rule)
                .SelectMany(group => group)]);
    }

    /// <summary>"Linha 5", "Linhas 3 a 7", "Linhas 5 e 14" ou "Linhas 3, 5 e 14".</summary>
    internal static string LinesLabel(IReadOnlyList<int> lines)
    {
        string Number(int line) => line.ToString(CultureInfo.InvariantCulture);
        switch (lines.Count)
        {
            case 0:
                return "";
            case 1:
                return $"Linha {Number(lines[0])}";
        }

        var consecutive = lines.Zip(lines.Skip(1), (current, next) => next - current).All(step => step == 1);
        if (consecutive && lines.Count > 2)
        {
            return $"Linhas {Number(lines[0])} a {Number(lines[^1])}";
        }

        return $"Linhas {string.Join(", ", lines.Take(lines.Count - 1).Select(Number))} e {Number(lines[^1])}";
    }
}
