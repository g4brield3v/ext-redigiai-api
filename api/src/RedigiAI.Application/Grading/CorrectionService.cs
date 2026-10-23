using System.Globalization;
using RedigiAI.Application.Abstractions;
using RedigiAI.Application.Essays;
using RedigiAI.Domain.Accounts;
using RedigiAI.Domain.Catalog;
using RedigiAI.Domain.Common;
using RedigiAI.Domain.Essays;
using RedigiAI.Domain.Grading;

namespace RedigiAI.Application.Grading;

/// <summary>A grade com que a correção foi feita.</summary>
/// <param name="MaxLevel">Nível mais alto de cada critério.</param>
/// <param name="VersionLabel">Como a grade aparece para o aluno, como "Fuvest 2027 (v1)".</param>
public sealed record CorrectionRubricDto(int Version, int MaxLevel, int MaxScore, string VersionLabel);

/// <param name="RuleCode">Código da hipótese de nota zero aplicada; nulo quando os critérios foram pontuados.</param>
/// <param name="RuleLabel">Texto da hipótese, como está na grade.</param>
/// <param name="RuleNote">O que a regra significa, escrito para o aluno.</param>
/// <param name="Explanation">Por que a hipótese se aplica a esta redação.</param>
public sealed record CorrectionZeroDto(bool Applies, string? RuleCode, string? RuleLabel, string? RuleNote, string? Explanation);

/// <summary>O problema principal ou um próximo passo.</summary>
/// <param name="Criterion">Posição do critério a que o texto se refere; nulo com nota zero ou em passo geral.</param>
/// <param name="PotentialGain">Pontos que o critério ainda pode render: (nível máximo − nível) × peso.</param>
public sealed record CorrectionPointDto(string Text, int? Criterion, int? PotentialGain);

/// <summary>Apontamento: um trecho marcado na redação, com o comentário.</summary>
/// <param name="Id">Número do apontamento na correção: 1, 2, 3…, em ordem de linha.</param>
/// <param name="Criterion">Posição do critério.</param>
/// <param name="CharStart">Posição no texto da linha inicial, incluída.</param>
/// <param name="CharEnd">Posição no texto da linha final, excluída.</param>
/// <param name="Quote">O trecho marcado, recortado da redação.</param>
/// <param name="RuleName">Nome da regra de escrita, como "Crase antes de verbo".</param>
/// <param name="Fix">Obsoleto: a forma correta, igual a <c>answer</c> quando <c>answerKind</c> é <c>exact</c>.</param>
/// <param name="Why">A regra geral por trás do desvio; nulo nos acertos e nas correções antigas.</param>
/// <param name="Answer">A forma correta (critério 4) ou uma reescrita possível (critérios 1 a 3).</param>
/// <param name="AnswerKind"><c>exact</c> no critério 4; <c>example</c> nos critérios 1 a 3.</param>
/// <param name="AskLabel">O pedido ao aluno antes de ver a resposta, até 60 caracteres; opcional.</param>
public sealed record CorrectionAnnotationDto(
    int Id,
    MarkKind Kind,
    int Criterion,
    int LineStart,
    int LineEnd,
    int CharStart,
    int CharEnd,
    string Quote,
    string? Note,
    string? RuleName,
    [property: Obsolete("Use answer, com answerKind = exact.")] string? Fix,
    string? Why,
    string? Answer,
    AnswerKind? AnswerKind,
    string? AskLabel);

/// <summary>Um pedaço do exemplo de reescrita.</summary>
/// <param name="Kind"><c>keep</c>: texto do aluno que fica; <c>remove</c>: texto do aluno que sai; <c>add</c>: texto novo.</param>
public sealed record CorrectionExampleSegmentDto(string Text, ExampleSegmentKind Kind);

/// <summary>Como ficaria um trecho do texto: um exemplo, não a única forma.</summary>
/// <param name="LineLabel">Onde fica o trecho, como "Fim do parágrafo, linha 29".</param>
public sealed record CorrectionExampleDto(string LineLabel, IReadOnlyList<CorrectionExampleSegmentDto> Segments);

/// <summary>Os desvios de um critério, na ordem em que o aluno deve olhar.</summary>
/// <param name="PotentialGain">Pontos perdidos no critério.</param>
/// <param name="MarkIds">Números dos apontamentos (<c>id</c>), em ordem de linha; no critério 4, os da mesma regra juntos.</param>
public sealed record CorrectionAnnotationGroupDto(int Criterion, int PotentialGain, IReadOnlyList<int> MarkIds);

/// <param name="Points">Nível × peso, calculado pelo backend.</param>
/// <param name="PointsLost">Pontos que faltam para a nota máxima do critério.</param>
/// <param name="CurrentDescriptor">Descritor oficial do nível atingido, da grade usada.</param>
/// <param name="NextDescriptor">Descritor do nível seguinte; nulo no nível máximo.</param>
/// <param name="Guidance">O que corrigir, sempre como afirmação; nulo quando o nível é o máximo.</param>
/// <param name="AnnotationIds">Apontamentos deste critério, em ordem de linha.</param>
/// <param name="PlainExplanation">O que muda para o próximo nível neste texto; nulo no nível máximo e nas correções antigas.</param>
/// <param name="Example">Como ficaria um trecho do texto, nos critérios 1 a 3 abaixo do nível máximo; opcional.</param>
public sealed record CorrectionCriterionDto(
    int Position,
    string ShortName,
    string FullName,
    int Weight,
    int Level,
    int Points,
    int MaxPoints,
    int PointsLost,
    string CurrentDescriptor,
    string? NextDescriptor,
    string Justification,
    string? Guidance,
    IReadOnlyList<int> AnnotationIds,
    string? PlainExplanation,
    CorrectionExampleDto? Example);

public sealed record CorrectionFeedbackDto(FeedbackRating Rating, string? Comment);

/// <param name="Title">"Vestibular – Tipo – Tema".</param>
/// <param name="Version">Versão da redação: 1 no primeiro envio, 2 na primeira reescrita.</param>
/// <param name="Score">Nota da grade, sempre calculada pelo backend.</param>
/// <param name="ScoreDelta">Diferença para a nota da versão anterior da mesma redação, se ela foi corrigida.</param>
/// <param name="PreviousVersionScore">Nota da versão anterior; nula na primeira versão.</param>
/// <param name="MainIssue">Por onde começar a reescrita.</param>
/// <param name="Lines">A redação como foi corrigida: a transcrição com as palavras digitadas pelo aluno.</param>
/// <param name="NextSteps">Plano de reescrita, do passo que mais aumenta a nota para o que menos aumenta.</param>
/// <param name="Feedback">A avaliação que o aluno fez desta correção, se já avaliou.</param>
/// <param name="AnnotationGroups">
/// Os desvios por critério, do que mais aumenta a nota para o que menos: o critério do problema principal, depois
/// os de mais pontos perdidos (no empate, o menor número) e o critério 4 por último. Vazio com nota zero.
/// </param>
public sealed record CorrectionDto(
    Guid Id,
    Guid EssayId,
    string Title,
    Guid PromptId,
    string PromptSlug,
    int PromptVersion,
    Guid EssayTypeId,
    DateTimeOffset SentAt,
    int Version,
    string ExamName,
    CorrectionRubricDto Rubric,
    int Score,
    int MaxScore,
    int? ScoreDelta,
    int? PreviousVersion,
    int? PreviousVersionScore,
    CorrectionZeroDto Zero,
    string Strengths,
    CorrectionPointDto MainIssue,
    int SheetLines,
    IReadOnlyList<TranscriptionLineDto> Lines,
    IReadOnlyList<CorrectionAnnotationDto> Annotations,
    IReadOnlyList<CorrectionCriterionDto> Criteria,
    IReadOnlyList<CorrectionPointDto> NextSteps,
    CorrectionFeedbackDto? Feedback,
    IReadOnlyList<CorrectionAnnotationGroupDto> AnnotationGroups);

public interface ICorrectionService
{
    /// <summary>A correção completa de uma redação do aluno.</summary>
    /// <exception cref="AppError">
    /// <c>ESSAY_NOT_FOUND</c> se a redação não é do aluno; <c>INVALID_STATE</c> se ainda não foi corrigida.
    /// </exception>
    Task<CorrectionDto> GetAsync(string clerkUserId, Guid essayId, CancellationToken ct);

    /// <summary>Grava a avaliação do aluno sobre a correção: uma por correção.</summary>
    /// <exception cref="AppError">
    /// <c>NOT_FOUND</c> se a correção não é do aluno; <c>VALIDATION_ERROR</c> sem opção escolhida ou com comentário
    /// longo demais; <c>INVALID_STATE</c> se a correção já foi avaliada.
    /// </exception>
    Task SendFeedbackAsync(string clerkUserId, Guid correctionId, FeedbackRating? rating, string? comment, CancellationToken ct);
}

internal sealed class CorrectionService(
    IUserRepository users,
    IEssayRepository essays,
    IExamRepository exams,
    IPromptRepository proposals,
    IRubricRepository rubrics,
    IGradingProfileRepository profiles,
    IWritingRulesRepository writingRules,
    ITranscriptionRepository transcriptions,
    ICorrectionRepository corrections,
    IUnitOfWork unitOfWork,
    TimeProvider time) : ICorrectionService
{
    public async Task<CorrectionDto> GetAsync(string clerkUserId, Guid essayId, CancellationToken ct)
    {
        var user = await users.FindByClerkIdAsync(clerkUserId, ct) ?? throw Errors.ProfileNotFound();
        var essay = await essays.FindAsync(essayId, user.Id, ct) ?? throw Errors.EssayNotFound();
        var correction = await corrections.FindByEssayAsync(essay.Id, ct) ?? throw Errors.InvalidState();

        var exam = await exams.FindByIdAsync(essay.ExamId, ct) ?? throw Errors.Internal();
        var proposal = (await proposals.ReferencesAsync([essay.PromptId], ct)).GetValueOrDefault(essay.PromptId) ?? throw Errors.Internal();
        var rubric = await rubrics.FindAsync(correction.RubricVersionId, ct) ?? throw Errors.Internal();
        var profile = await profiles.FindAsync(correction.GradingProfileId, ct) ?? throw Errors.Internal();
        var rules = await writingRules.FindAsync(profile.WritingRulesVersionId, ct);
        var transcription = await transcriptions.FindByEssayAsync(essay.Id, ct) ?? throw Errors.Internal();
        var feedback = await corrections.FindFeedbackAsync(correction.Id, ct);
        var previous = await PreviousVersionAsync(essay, user.Id, ct);

        var lines = transcription.FinalLines();
        var textByLine = lines.ToDictionary(line => line.Number, line => line.Text);
        var ruleNames = (rules?.Rules ?? []).ToDictionary(rule => rule.Code, rule => rule.Name);
        var zeroRule = rubric.ZeroRules.FirstOrDefault(rule => rule.Code == correction.ZeroReason);

        // O número do apontamento é a posição dele na leitura da folha.
        var annotations = correction.Marks
            .OrderBy(mark => mark.LineStart)
            .ThenBy(mark => mark.CharStart)
            // Desempate fixo: o número de um apontamento não muda de uma leitura para outra.
            .ThenBy(mark => mark.CharEnd)
            .ThenBy(mark => mark.Id)
            .Select((mark, index) => new CorrectionAnnotationDto(
                index + 1,
                mark.Kind,
                mark.CriterionPosition,
                mark.LineStart,
                mark.LineEnd,
                mark.CharStart,
                mark.CharEnd,
                Quote(mark, textByLine),
                mark.Note,
                mark.RuleCode is { } code ? ruleNames.GetValueOrDefault(code, code) : null,
                mark.Fix,
                mark.Why,
                mark.Answer,
                mark.AnswerKind,
                mark.AskLabel))
            .ToList();

        // Quanto cada critério ainda pode render; vazio com nota zero, quando nenhum é pontuado.
        var gainByCriterion = correction.Criteria.ToDictionary(
            graded => graded.Position,
            graded => rubric.Criteria.Single(c => c.Id == graded.CriterionId).PointsLostAt(graded.Level));
        CorrectionPointDto Point(string text, int? criterion) =>
            criterion is { } position && gainByCriterion.TryGetValue(position, out var gain)
                ? new CorrectionPointDto(text, position, gain)
                : new CorrectionPointDto(text, null, null);

        return new CorrectionDto(
            correction.Id,
            essay.Id,
            essay.Title,
            essay.PromptId,
            proposal.Slug,
            proposal.Version,
            essay.EssayTypeId,
            essay.CreatedAt,
            essay.Version,
            exam.Name,
            new CorrectionRubricDto(rubric.Version, rubric.MaxLevel, rubric.MaxScore, VersionLabel(rubric, exam)),
            correction.Score,
            correction.MaxScore,
            previous is null ? null : correction.Score - previous.Value.Score,
            previous?.Version,
            previous?.Score,
            new CorrectionZeroDto(
                correction.ZeroReason is not null,
                correction.ZeroReason,
                zeroRule?.Description,
                string.IsNullOrWhiteSpace(zeroRule?.StudentNote) ? null : zeroRule.StudentNote,
                correction.ZeroExplanation),
            correction.Strengths,
            Point(correction.MainIssue, correction.MainIssueCriterion),
            exam.SheetLines,
            [.. lines.Select(line => new TranscriptionLineDto(line.Number, line.Text, line.Indent))],
            annotations,
            [.. correction.Criteria.OrderBy(c => c.Position).Select(graded => ToDto(graded, rubric, annotations))],
            // A ordenação é estável: passos de mesmo ganho ficam na ordem em que a IA os escreveu.
            [.. correction.NextSteps.Select(step => Point(step.Text, step.Criterion)).OrderByDescending(step => step.PotentialGain ?? -1)],
            feedback is null ? null : new CorrectionFeedbackDto(feedback.Rating, feedback.Comment),
            AnnotationGroups(annotations, gainByCriterion, correction.MainIssueCriterion));
    }

    /// <summary>
    /// Os desvios agrupados por critério, do que mais aumenta a nota para o que menos. Com nota zero nenhum
    /// critério é pontuado e não há ganho a ordenar: a lista vem vazia.
    /// </summary>
    internal static List<CorrectionAnnotationGroupDto> AnnotationGroups(
        IReadOnlyList<CorrectionAnnotationDto> annotations, IReadOnlyDictionary<int, int> gainByCriterion, int? mainIssueCriterion)
    {
        return
        [
            .. annotations
                .Where(annotation => annotation.Kind == MarkKind.Issue && gainByCriterion.ContainsKey(annotation.Criterion))
                .GroupBy(annotation => annotation.Criterion)
                .OrderBy(group => group.Key == mainIssueCriterion ? 0 : group.Key == Correction.WritingCriterion ? 2 : 1)
                .ThenByDescending(group => gainByCriterion[group.Key])
                .ThenBy(group => group.Key)
                .Select(group => new CorrectionAnnotationGroupDto(
                    group.Key,
                    gainByCriterion[group.Key],
                    group.Key == Correction.WritingCriterion ? ByRule(group) : [.. group.Select(annotation => annotation.Id).Order()])),
        ];

        // Os desvios da mesma regra ficam juntos, na ordem de linha da primeira ocorrência; o id já é a ordem de linha.
        static List<int> ByRule(IEnumerable<CorrectionAnnotationDto> group) =>
        [
            .. group
                .GroupBy(annotation => annotation.RuleName)
                .OrderBy(rule => rule.Min(annotation => annotation.Id))
                .SelectMany(rule => rule.Select(annotation => annotation.Id).Order()),
        ];
    }

    public async Task SendFeedbackAsync(
        string clerkUserId, Guid correctionId, FeedbackRating? rating, string? comment, CancellationToken ct)
    {
        var user = await users.FindByClerkIdAsync(clerkUserId, ct) ?? throw Errors.ProfileNotFound();
        var correction = await corrections.FindAsync(correctionId, ct) ?? throw Errors.NotFound();

        // Correção de redação de outro aluno responde como se não existisse.
        _ = await essays.FindAsync(correction.EssayId, user.Id, ct) ?? throw Errors.NotFound();

        if (rating is not { } chosen || !Enum.IsDefined(chosen))
        {
            throw Errors.Validation("Escolha uma das opções para avaliar a correção.");
        }

        if (!CorrectionFeedback.AcceptsComment(comment))
        {
            throw Errors.Validation(
                $"O comentário pode ter até {CorrectionFeedback.MaxCommentLength.ToString(CultureInfo.InvariantCulture)} caracteres, só com texto.");
        }

        if (await corrections.FindFeedbackAsync(correction.Id, ct) is not null)
        {
            throw Errors.InvalidState();
        }

        corrections.AddFeedback(CorrectionFeedback.Create(correction.Id, chosen, comment, time.GetUtcNow()));
        try
        {
            await unitOfWork.SaveChangesAsync(ct);
        }
        catch (UniqueConflictException)
        {
            // Dois envios ao mesmo tempo: o índice único deixa passar só um.
            throw Errors.InvalidState();
        }
    }

    /// <summary>Uma reescrita é comparada com a versão anterior, se ela chegou a ser corrigida.</summary>
    private async Task<(int Version, int Score)?> PreviousVersionAsync(Essay essay, Guid userId, CancellationToken ct)
    {
        if (essay.ParentEssayId is not { } parentId
            || await essays.FindAsync(parentId, userId, ct) is not { } parent
            || !(await corrections.ScoresAsync([parent.Id], ct)).TryGetValue(parent.Id, out var previous))
        {
            return null;
        }

        return (parent.Version, previous.Score);
    }

    private static CorrectionCriterionDto ToDto(
        CorrectionCriterion graded, RubricVersion rubric, List<CorrectionAnnotationDto> annotations)
    {
        var criterion = rubric.Criteria.Single(c => c.Id == graded.CriterionId);
        return new CorrectionCriterionDto(
            graded.Position,
            criterion.ShortName,
            criterion.Name,
            criterion.Weight,
            graded.Level,
            graded.Points,
            criterion.MaxPoints,
            criterion.PointsLostAt(graded.Level),
            criterion.LevelDescriptors[graded.Level],
            criterion.LevelDescriptors.GetValueOrDefault(graded.Level + 1),
            graded.Justification,
            graded.Guidance,
            [.. annotations.Where(annotation => annotation.Criterion == graded.Position).Select(annotation => annotation.Id)],
            graded.PlainExplanation,
            graded.Example is { } example
                ? new CorrectionExampleDto(
                    example.LineLabel, [.. example.Segments.Select(segment => new CorrectionExampleSegmentDto(segment.Text, segment.Kind))])
                : null);
    }

    /// <summary>"Fuvest 2027 (v1)"; grade sem rótulo leva o nome do vestibular.</summary>
    internal static string VersionLabel(RubricVersion rubric, Exam exam) =>
        $"{(string.IsNullOrWhiteSpace(rubric.Label) ? exam.Name : rubric.Label)} (v{rubric.Version.ToString(CultureInfo.InvariantCulture)})";

    /// <summary>
    /// O trecho marcado, exatamente como está na redação. Num trecho que atravessa linhas, vai do início marcado
    /// até o fim da primeira linha, as linhas do meio inteiras e a última até o fim marcado; as linhas são
    /// unidas por um espaço.
    /// </summary>
    internal static string Quote(CorrectionMark mark, IReadOnlyDictionary<int, string> textByLine)
    {
        if (mark.LineStart == mark.LineEnd)
        {
            var line = textByLine.GetValueOrDefault(mark.LineStart, "");
            var from = Math.Clamp(mark.CharStart, 0, line.Length);
            return line[from..Math.Clamp(mark.CharEnd, from, line.Length)];
        }

        var parts = new List<string>();
        for (var number = mark.LineStart; number <= mark.LineEnd; number++)
        {
            var text = textByLine.GetValueOrDefault(number, "");
            var start = number == mark.LineStart ? Math.Clamp(mark.CharStart, 0, text.Length) : 0;
            var end = number == mark.LineEnd ? Math.Clamp(mark.CharEnd, start, text.Length) : text.Length;
            parts.Add(text[start..end].Trim());
        }

        return string.Join(' ', parts.Where(part => part.Length > 0));
    }
}
