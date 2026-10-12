using RedigiAI.Domain.Catalog;
using RedigiAI.Domain.Essays;
using RedigiAI.Domain.Grading;

namespace RedigiAI.UnitTests.Grading;

public class CorrectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 15, 0, 0, TimeSpan.Zero);

    private static readonly RubricVersion Rubric = RubricVersion.Publish(
        Guid.CreateVersion7(),
        version: 1,
        maxLevel: 4,
        Now,
        [new ZeroRule("theme_not_addressed", "Não aborda o tema."), new ZeroRule("candidate_identification", "Identificação.")],
        [.. new[] { 3, 2, 3, 2 }.Select((weight, index) => new CriterionSpec(
            $"Critério {index + 1}",
            weight,
            Enumerable.Range(1, 4).ToDictionary(level => level, level => $"Descritor {level}")))]);

    private static readonly IReadOnlyList<EssayLine> Lines =
    [
        new(1, "A tecnologia mudou a forma de debater.", true),
        new(2, "À partir desse ponto, os algoritmos escolhem.", false),
        new(3, "", false),
    ];

    private static GradingContext Context(bool identificationFound = false) =>
        new(
            Guid.CreateVersion7(), Rubric, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Lines, identificationFound,
            new HashSet<string> { "CRASE_VERBO", WritingRulesVersion.OtherCode });

    private static GradedCriterion Criterion(int position, int level = 3, string? guidance = "Troque o parágrafo da linha 2 por um argumento próprio.") =>
        new(position, level, "Justificativa com as linhas 1 e 2.", [1, 2], guidance);

    private static GradingOutput Output(
        IReadOnlyList<GradedCriterion>? criteria = null,
        IReadOnlyList<GradedMark>? marks = null,
        IReadOnlyList<string>? nextSteps = null,
        GradedZero? zero = null,
        GradedPoint? mainIssue = null,
        int? stepCriterion = 3) =>
        new(
            zero ?? new GradedZero(false, null, null),
            criteria ?? [Criterion(1, 3), Criterion(2, 3), Criterion(3, 2), Criterion(4, 3)],
            marks ?? [new GradedMark(4, MarkKind.Issue, 2, 0, 8, "\"A partir\", sem crase", "CRASE_VERBO")],
            "Tese clara.",
            mainIssue ?? new GradedPoint("Poucos argumentos próprios.", 1),
            [.. (nextSteps ?? ["Releia a coletânea.", "Reescreva o segundo parágrafo."]).Select(text => new GradedPoint(text, stepCriterion))]);

    private static Correction Grade(GradingOutput output, GradingContext? context = null) =>
        Correction.Grade(context ?? Context(), output, "test-model", "{}", Now);

    private static void AssertRejected(int rule, GradingOutput output, GradingContext? context = null) =>
        Assert.Equal(rule, Assert.Throws<InvalidGradingException>(() => Grade(output, context)).Rule);

    [Fact]
    public void Score_is_the_sum_of_level_times_weight_computed_by_the_backend()
    {
        var correction = Grade(Output());

        // 3×3 + 3×2 + 2×3 + 3×2
        Assert.Equal((27, 40), (correction.Score, correction.MaxScore));
        Assert.Equal([9, 6, 6, 6], correction.Criteria.Select(c => c.Points));
        Assert.Null(correction.ZeroReason);
        var mark = Assert.Single(correction.Marks);
        Assert.Equal((4, 2, 0, 8, "CRASE_VERBO"), (mark.CriterionPosition, mark.LineStart, mark.CharStart, mark.CharEnd, mark.RuleCode));
    }

    [Fact]
    public void Top_level_in_every_criterion_gives_the_maximum_score()
    {
        var correction = Grade(Output(criteria: [.. Enumerable.Range(1, 4).Select(position => Criterion(position, 4, guidance: null))]));

        Assert.Equal(40, correction.Score);
        Assert.All(correction.Criteria, criterion => Assert.Null(criterion.Guidance));
    }

    [Fact]
    public void Missing_or_repeated_criterion_is_rejected()
    {
        AssertRejected(2, Output(criteria: [Criterion(1), Criterion(2), Criterion(3)]));
        AssertRejected(2, Output(criteria: [Criterion(1), Criterion(2), Criterion(3), Criterion(3)]));
        AssertRejected(2, Output(criteria: [Criterion(1), Criterion(2), Criterion(3), Criterion(5)]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Level_outside_the_rubric_is_rejected(int level) =>
        AssertRejected(2, Output(criteria: [Criterion(1, level), Criterion(2), Criterion(3), Criterion(4)]));

    [Fact]
    public void Line_or_position_that_is_not_in_the_essay_is_rejected()
    {
        AssertRejected(3, Output(criteria: [Criterion(1) with { Lines = [1, 31] }, Criterion(2), Criterion(3), Criterion(4)]));
        AssertRejected(3, Output(marks: [new GradedMark(4, MarkKind.Issue, 9, 0, 3, null, null)]));
        AssertRejected(3, Output(marks: [new GradedMark(4, MarkKind.Issue, 2, 40, 80, null, null)]));
        AssertRejected(3, Output(marks: [new GradedMark(4, MarkKind.Issue, 2, 5, 5, null, null)]));
    }

    [Fact]
    public void Guidance_or_next_step_as_a_question_is_rejected()
    {
        AssertRejected(4, Output(criteria: [Criterion(1, guidance: "Que tal rever a tese?"), Criterion(2), Criterion(3), Criterion(4)]));
        AssertRejected(4, Output(nextSteps: ["Você já pensou em reler a coletânea? Faça isso."]));
    }

    [Fact]
    public void More_than_12_marks_is_rejected() =>
        AssertRejected(1, Output(marks: [.. Enumerable.Repeat(new GradedMark(1, MarkKind.Good, 1, 0, 3, null, null), 13)]));

    [Fact]
    public void Rule_outside_the_catalog_is_recorded_as_other()
    {
        var invented = new string('X', 200);
        var correction = Grade(Output(marks:
        [
            new GradedMark(4, MarkKind.Issue, 2, 0, 8, "nota", invented),
            new GradedMark(4, MarkKind.Issue, 1, 0, 1, "nota", " CRASE_VERBO "),
            new GradedMark(1, MarkKind.Good, 1, 0, 1, null, "  "),
        ]));

        Assert.Equal([WritingRulesVersion.OtherCode, "CRASE_VERBO", null], correction.Marks.Select(mark => mark.RuleCode));
    }

    [Fact]
    public void Correct_form_is_kept_only_for_writing_deviations()
    {
        var correction = Grade(Output(marks:
        [
            new GradedMark(4, MarkKind.Issue, 2, 0, 8, "sem crase", "CRASE_VERBO", "A partir"),
            new GradedMark(3, MarkKind.Issue, 1, 0, 1, "repete a ideia", null, "Outra frase"),
            new GradedMark(1, MarkKind.Good, 1, 2, 12, "bom repertório", "CRASE_VERBO", "tecnologia"),
            new GradedMark(4, MarkKind.Issue, 2, 9, 14, "vago", "OUTRO", "  "),
        ]));

        Assert.Equal(["A partir", null, null, null], correction.Marks.Select(mark => mark.Fix));
    }

    [Fact]
    public void Main_issue_and_next_steps_point_to_criteria_of_the_rubric()
    {
        AssertRejected(2, Output(mainIssue: new GradedPoint("Poucos argumentos próprios.", null)));
        AssertRejected(2, Output(mainIssue: new GradedPoint("Poucos argumentos próprios.", 5)));
        AssertRejected(2, Output(stepCriterion: 9));

        // Um passo pode ser geral, sem critério.
        var correction = Grade(Output(stepCriterion: null));
        Assert.Equal(1, correction.MainIssueCriterion);
        Assert.All(correction.NextSteps, step => Assert.Null(step.Criterion));
        Assert.Equal([3, 3], Grade(Output()).NextSteps.Select(step => step.Criterion));
    }

    [Fact]
    public void Main_issue_is_a_statement_about_a_criterion_that_can_still_gain_points()
    {
        AssertRejected(4, Output(mainIssue: new GradedPoint("Que tal trazer argumentos próprios?", 1)));

        // O critério 2 já está no nível máximo e outros não: começar por ele não rende nada.
        var oneAtTop = new[] { Criterion(1, 3), Criterion(2, 4, guidance: null), Criterion(3, 2), Criterion(4, 3) };
        AssertRejected(2, Output(criteria: oneAtTop, mainIssue: new GradedPoint("Estrutura.", 2)));
        Assert.Equal(3, Grade(Output(criteria: oneAtTop, mainIssue: new GradedPoint("Coesão.", 3))).MainIssueCriterion);

        // Com tudo no nível máximo não há critério com pontos a ganhar: qualquer um é aceito.
        var allAtTop = Enumerable.Range(1, 4).Select(position => Criterion(position, 4, guidance: null)).ToList();
        Assert.Equal(40, Grade(Output(criteria: allAtTop, mainIssue: new GradedPoint("Nada a corrigir.", 2))).Score);
    }

    [Fact]
    public void Points_lost_are_what_is_missing_for_the_top_level()
    {
        var third = Rubric.Criteria.Single(criterion => criterion.Position == 3);

        Assert.Equal((6, 0, 9), (third.PointsLostAt(2), third.PointsLostAt(4), third.PointsLostAt(1)));
        // Sem nome curto, vale o nome completo.
        Assert.Equal("Critério 3", third.ShortName);
    }

    [Fact]
    public void More_than_3_next_steps_is_rejected() =>
        AssertRejected(5, Output(nextSteps: ["Um.", "Dois.", "Três.", "Quatro."]));

    [Fact]
    public void Zero_applies_only_with_a_rule_of_the_rubric_and_gives_score_zero()
    {
        AssertRejected(6, Output(zero: new GradedZero(true, "texto_curto", "Inventada.")));
        AssertRejected(6, Output(zero: new GradedZero(true, null, null)));

        var correction = Grade(Output(zero: new GradedZero(true, "theme_not_addressed", "O texto trata de outro assunto.")));

        Assert.Equal((0, 40, "theme_not_addressed"), (correction.Score, correction.MaxScore, correction.ZeroReason));
        // Com nota zero os critérios não são pontuados, mesmo que a IA os envie.
        Assert.Empty(correction.Criteria);
        // E nada fica ligado a critério: não há pontos a ganhar em um só.
        Assert.Null(correction.MainIssueCriterion);
        Assert.All(correction.NextSteps, step => Assert.Null(step.Criterion));
    }

    [Fact]
    public void Identification_on_the_sheet_without_zero_is_rejected()
    {
        AssertRejected(7, Output(), Context(identificationFound: true));

        var correction = Grade(
            Output(zero: new GradedZero(true, "candidate_identification", "Há um nome no fim da folha.")),
            Context(identificationFound: true));
        Assert.Equal(0, correction.Score);
    }

    [Theory]
    [InlineData("Troque o parágrafo das linhas 11 a 13 por um exemplo.", false)]
    [InlineData("Que tal rever a tese?", true)]
    [InlineData("Por que não citar a coletânea? Cite o texto 2.", true)]
    [InlineData("Troque \"por quê?\" por \"porque\" na linha 4.", false)]
    [InlineData("Na linha 9, “E agora?” funciona como gancho; mantenha.", false)]
    [InlineData("Reescreva a frase \"Como evitar isso?\"", true)]
    [InlineData("Reveja o trecho \"e agora? sem fechar as aspas.", true)]
    public void Question_is_a_text_ending_with_or_having_a_question_mark_outside_quotes(string text, bool question) =>
        Assert.Equal(question, Feedback.IsQuestion(text));

    [Fact]
    public void Final_lines_replace_each_marker_with_what_the_student_typed()
    {
        var transcription = Transcription.Create(
            Guid.CreateVersion7(),
            30,
            [new ReadLine(1, "Os [?] decidem o que [?] vemos", true), new ReadLine(2, "sem marcador", false)],
            identificationFound: false,
            "test-model",
            Now);
        transcription.Words[0].Fill("algoritmos", Now);
        transcription.Words[1].Fill("nós", Now);

        Assert.Equal(
            ["Os algoritmos decidem o que nós vemos", "sem marcador"],
            transcription.FinalLines().Select(line => line.Text));
        // A transcrição continua como a IA leu.
        Assert.Equal("Os [?] decidem o que [?] vemos", transcription.Lines[0].Text);
    }
}
