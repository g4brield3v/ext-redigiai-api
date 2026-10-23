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

    private static GradingContext Context(
        bool identificationFound = false, IReadOnlyList<CopiedPassage>? copied = null, bool copyRequiresZero = false) =>
        new(
            Guid.CreateVersion7(), Rubric, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Lines, identificationFound,
            new HashSet<string> { "CRASE_VERBO", WritingRulesVersion.OtherCode }, copied ?? [], copyRequiresZero);

    private static GradedCriterion Criterion(int position, int level = 3, string? guidance = "Troque o parágrafo da linha 2 por um argumento próprio.") =>
        new(position, level, "Justificativa com as linhas 1 e 2.", [1, 2], guidance);

    /// <summary>Um desvio completo: comentário, regra geral e resposta, sem a resposta no comentário.</summary>
    private static GradedMark Issue(
        int criterion,
        int line,
        int start,
        int end,
        string? note = "nota",
        string? rule = null,
        string? why = "Antes de verbo não há artigo, e sem artigo não há crase.",
        string? answer = "A partir",
        string? askLabel = null) =>
        new(criterion, MarkKind.Issue, line, start, end, note, rule, why, answer, askLabel);

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
            marks ?? [Issue(4, 2, 0, 8, "Crase antes de verbo.", "CRASE_VERBO")],
            "Tese clara.",
            mainIssue ?? new GradedPoint("Poucos argumentos próprios.", 1),
            [.. (nextSteps ?? ["Releia a coletânea.", "Reescreva o segundo parágrafo."]).Select(text => new GradedPoint(text, stepCriterion))]);

    private static Correction Grade(GradingOutput output, GradingContext? context = null) =>
        Correction.Grade(context ?? Context(), output, "test-model", "{}", Now);

    private static void AssertRejected(int rule, GradingOutput output, GradingContext? context = null) =>
        Assert.Equal(rule, Assert.Throws<InvalidGradingException>(() => Grade(output, context)).Rule);

    private static void AssertRejectedWith(string code, GradingOutput output) =>
        Assert.Equal(code, Assert.Throws<InvalidGradingException>(() => Grade(output)).Code);

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
    public void Many_distinct_marks_up_to_the_cap_are_all_kept() =>
        Assert.Equal(Correction.MaxMarks, Grade(Output(marks: DistinctMarks(Correction.MaxMarks, MarkKind.Issue))).Marks.Count);

    [Fact]
    public void Repeated_mark_is_kept_once_without_rejecting_the_correction()
    {
        var correction = Grade(Output(marks: [.. Enumerable.Repeat(new GradedMark(1, MarkKind.Good, 1, 0, 3, null, null), 40)]));

        var mark = Assert.Single(correction.Marks);
        Assert.Equal((1, 1, 0, 3), (mark.CriterionPosition, mark.LineStart, mark.CharStart, mark.CharEnd));
    }

    [Fact]
    public void Above_the_cap_the_marks_that_cost_points_come_first_in_the_original_order()
    {
        // 60 acertos e depois 30 desvios: passam do teto de 80, então saem 10 acertos e nenhum desvio.
        var good = DistinctMarks(60, MarkKind.Good);
        var issues = DistinctMarks(30, MarkKind.Issue, firstLine: 2);

        var correction = Grade(Output(marks: [.. good, .. issues]));

        Assert.Equal(Correction.MaxMarks, correction.Marks.Count);
        Assert.Equal(30, correction.Marks.Count(mark => mark.Kind == MarkKind.Issue));
        Assert.Equal(
            [.. good.Take(50).Concat(issues).Select(mark => (mark.Line, mark.Start, mark.Criterion))],
            correction.Marks.Select(mark => (mark.LineStart, mark.CharStart, mark.CriterionPosition)));
    }

    [Fact]
    public void Mark_beyond_the_cap_with_a_made_up_position_still_rejects_the_response() =>
        AssertRejected(3, Output(marks: [.. DistinctMarks(Correction.MaxMarks, MarkKind.Issue), new GradedMark(1, MarkKind.Good, 9, 0, 3, null, null)]));

    /// <summary>Marcas em trechos diferentes: de um em um caractere, revezando os quatro critérios.</summary>
    private static List<GradedMark> DistinctMarks(int count, MarkKind kind, int firstLine = 1) =>
        [.. Enumerable.Range(0, count).Select(i => kind == MarkKind.Issue
            ? Issue(1 + (i % 4), firstLine, i / 4, (i / 4) + 1)
            : new GradedMark(1 + (i % 4), kind, firstLine, i / 4, (i / 4) + 1, "nota", null))];
    [Fact]
    public void Rule_outside_the_catalog_is_recorded_as_other()
    {
        var invented = new string('X', 200);
        var correction = Grade(Output(marks:
        [
            Issue(4, 2, 0, 8, rule: invented),
            Issue(4, 1, 0, 1, rule: " CRASE_VERBO "),
            new GradedMark(1, MarkKind.Good, 1, 0, 1, null, "  "),
        ]));

        Assert.Equal([WritingRulesVersion.OtherCode, "CRASE_VERBO", null], correction.Marks.Select(mark => mark.RuleCode));
    }

    [Fact]
    public void Answer_is_exact_in_criterion_4_and_an_example_elsewhere_and_fix_repeats_the_exact_answer()
    {
        var correction = Grade(Output(marks:
        [
            Issue(4, 2, 0, 8, "sem crase", "CRASE_VERBO", askLabel: "  Escreva a forma correta  "),
            Issue(3, 1, 0, 1, "repete a ideia", answer: "Outra frase, com um dado novo."),
            new GradedMark(1, MarkKind.Good, 1, 2, 12, "bom repertório", null, "Regra.", "tecnologia", "Escreva"),
        ]));

        Assert.Equal(["A partir", null, null], correction.Marks.Select(mark => mark.Fix));
        Assert.Equal([AnswerKind.Exact, AnswerKind.Example, null], correction.Marks.Select(mark => mark.AnswerKind));
        Assert.Equal(["A partir", "Outra frase, com um dado novo.", null], correction.Marks.Select(mark => mark.Answer));
        Assert.Equal(["Escreva a forma correta", null, null], correction.Marks.Select(mark => mark.AskLabel));

        // Num acerto, os campos de aprendizado são descartados.
        Assert.Null(correction.Marks[2].Why);
    }

    [Fact]
    public void An_issue_without_why_or_answer_is_rejected_as_missing_field()
    {
        AssertRejectedWith(GradingRejections.MissingField, Output(marks: [Issue(4, 2, 0, 8, why: null)]));
        AssertRejectedWith(GradingRejections.MissingField, Output(marks: [Issue(4, 2, 0, 8, answer: "  ")]));
    }

    [Fact]
    public void Note_or_why_that_gives_the_answer_is_rejected_as_leaked()
    {
        // Sem contar maiúsculas, aspas e pontuação final.
        AssertRejectedWith(GradingRejections.AnswerLeaked, Output(marks: [Issue(4, 2, 0, 8, note: "Escreva “a partir.”")]));
        AssertRejectedWith(GradingRejections.AnswerLeaked, Output(marks: [Issue(4, 2, 0, 8, why: "O certo é A PARTIR, sem crase.")]));

        // Só palavras inteiras contam: "apartir" não é "a partir".
        Assert.Single(Grade(Output(marks: [Issue(4, 2, 0, 8, note: "Não junte em apartir.")])).Marks);

        // Resposta curta de uma palavra ("à") aparece por acaso em qualquer explicação.
        Assert.Single(Grade(Output(marks: [Issue(4, 2, 0, 8, why: "Antes de palavra feminina, à indica crase.", answer: "à")])).Marks);
    }

    [Fact]
    public void Learning_fields_are_statements_and_the_ask_label_is_short()
    {
        AssertRejected(4, Output(marks: [Issue(4, 2, 0, 8, why: "Qual é a regra?")]));
        AssertRejected(4, Output(marks: [Issue(4, 2, 0, 8, answer: "A partir?")]));
        AssertRejected(4, Output(marks: [Issue(4, 2, 0, 8, askLabel: "Como você escreveria?")]));
        AssertRejected(1, Output(marks: [Issue(4, 2, 0, 8, askLabel: new string('a', Correction.MaxAskLabelLength + 1))]));
        AssertRejected(4, Output(criteria: [Criterion(1, 3) with { PlainExplanation = "O que falta?" }, Criterion(2, 3), Criterion(3, 2), Criterion(4, 3)]));
    }

    [Fact]
    public void Answer_may_be_a_question_when_the_marked_text_already_is_one()
    {
        var lines = new List<EssayLine> { new(1, "Por que? Ninguém sabe.", true), new(2, "E segue.", false) };
        var context = Context() with { Lines = lines };
        var output = Output(marks:
        [
            Issue(4, 1, 0, 8, note: "Pergunta no fim da frase.", why: "No fim da pergunta, o porquê é separado e acentuado.", answer: "Por quê?"),
        ]);

        Assert.Equal("Por quê?", Grade(output, context).Marks.Single().Answer);
    }

    [Fact]
    public void Profile_with_the_previous_instructions_is_not_rejected_for_missing_learning_fields()
    {
        var context = Context() with { LearningFieldsRequired = false };
        var correction = Grade(Output(marks: [Issue(4, 2, 0, 8, why: null, answer: null)]), context);

        var mark = correction.Marks.Single();
        Assert.Null(mark.Why);
        Assert.Null(mark.Answer);
        Assert.Null(mark.AnswerKind);
        Assert.Null(mark.Fix);
    }

    [Fact]
    public void Learning_fields_too_long_are_rejected()
    {
        var huge = new string('a', Correction.MaxLearningTextLength + 1);
        AssertRejected(1, Output(marks: [Issue(4, 2, 0, 8, why: huge)]));
        AssertRejected(1, Output(criteria: [Criterion(1, 3) with { PlainExplanation = huge }, Criterion(2, 3), Criterion(3, 2), Criterion(4, 3)]));
        var manySegments = Example([.. Enumerable.Repeat(("À ", ExampleSegmentKind.Keep), Correction.MaxExampleSegments + 1)]);
        AssertRejected(1, Output(criteria: [Criterion(1, 3) with { Example = manySegments }, Criterion(2, 3), Criterion(3, 2), Criterion(4, 3)]));
    }

    [Fact]
    public void Example_copied_from_what_the_model_read_matches_the_angle_brackets()
    {
        // A IA lê "<" e ">" do aluno como "＜" e "＞".
        var context = Context() with { Lines = [new(1, "A rede <social> decide tudo.", true), new(2, "E segue.", false)] };
        var output = Output(
            criteria: [Criterion(1, 3) with { Example = Example(("A rede ＜social＞ ", ExampleSegmentKind.Keep), ("decide", ExampleSegmentKind.Remove)) }, Criterion(2, 3), Criterion(3, 2), Criterion(4, 3)],
            marks: []);

        Assert.NotNull(Grade(output, context).Criteria[0].Example);
    }

    private static CriterionExample Example(params (string Text, ExampleSegmentKind Kind)[] segments) =>
        new("Linha 2", [.. segments.Select(segment => new ExampleSegment(segment.Text, segment.Kind))]);

    [Fact]
    public void Example_must_start_from_the_text_of_the_essay()
    {
        var example = Example(
            ("À partir desse ponto, ", ExampleSegmentKind.Keep),
            ("os algoritmos", ExampleSegmentKind.Remove),
            ("as plataformas", ExampleSegmentKind.Add),
            ("  escolhem.", ExampleSegmentKind.Keep));
        var correction = Grade(Output(criteria:
        [
            Criterion(1, 3) with { PlainExplanation = "Falta um argumento próprio na linha 2.", Example = example },
            Criterion(2, 3),
            Criterion(3, 2),
            Criterion(4, 3),
        ]));

        var first = correction.Criteria.Single(criterion => criterion.Position == 1);
        Assert.Equal(example, first.Example);
        Assert.Equal("Falta um argumento próprio na linha 2.", first.PlainExplanation);

        // O texto mantido e tirado não está na redação.
        AssertRejectedWith(GradingRejections.ExampleMismatch, Output(criteria:
        [
            Criterion(1, 3) with { Example = Example(("os robôs escolhem", ExampleSegmentKind.Remove), ("nós", ExampleSegmentKind.Add)) },
            Criterion(2, 3),
            Criterion(3, 2),
            Criterion(4, 3),
        ]));

        // Só texto novo, sem nada da redação: não há de onde partir.
        AssertRejectedWith(GradingRejections.ExampleMismatch, Output(criteria:
        [
            Criterion(1, 3) with { Example = Example(("Um parágrafo novo.", ExampleSegmentKind.Add)) },
            Criterion(2, 3),
            Criterion(3, 2),
            Criterion(4, 3),
        ]));
    }

    [Fact]
    public void Example_crosses_lines_and_joins_a_word_split_by_a_hyphen()
    {
        var lines = new List<EssayLine>
        {
            new(1, "A tecnologia mudou a for-", true),
            new(2, "ma de debater.", false),
        };
        var context = Context() with { Lines = lines };
        GradingOutput WithExample(string text) => Output(
            criteria:
            [
                Criterion(1, 3) with { Example = Example((text, ExampleSegmentKind.Keep), ("hoje", ExampleSegmentKind.Add)) },
                Criterion(2, 3),
                Criterion(3, 2),
                Criterion(4, 3),
            ],
            marks: []);

        Assert.NotNull(Grade(WithExample("mudou a forma de debater."), context).Criteria[0].Example);
        Assert.NotNull(Grade(WithExample("mudou a for- ma de debater."), context).Criteria[0].Example);
    }

    [Fact]
    public void Explanation_and_example_are_dropped_at_the_top_level_and_the_example_in_criterion_4()
    {
        var example = Example(("os algoritmos escolhem", ExampleSegmentKind.Remove), ("as pessoas escolhem", ExampleSegmentKind.Add));
        var correction = Grade(Output(criteria:
        [
            Criterion(1, 3),
            Criterion(2, 4, guidance: null) with { PlainExplanation = "Está no nível máximo.", Example = example },
            Criterion(3, 2),
            Criterion(4, 3) with { PlainExplanation = "Revise a crase da linha 2.", Example = example },
        ]));

        var top = correction.Criteria.Single(criterion => criterion.Position == 2);
        var writing = correction.Criteria.Single(criterion => criterion.Position == 4);
        Assert.Null(top.PlainExplanation);
        Assert.Null(top.Example);
        Assert.Equal("Revise a crase da linha 2.", writing.PlainExplanation);
        Assert.Null(writing.Example);
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
    public void Essay_mostly_copied_from_the_collection_without_zero_is_rejected()
    {
        CopiedPassage[] copied = [new("Texto 2", 1, 2, 40, "Somente numa sociedade onde exista um clima cultural")];

        AssertRejected(8, Output(), Context(copied: copied, copyRequiresZero: true));

        var zeroed = Grade(
            Output(zero: new GradedZero(true, "theme_not_addressed", "O texto é cópia da coletânea.")),
            Context(copied: copied, copyRequiresZero: true));
        Assert.Equal(0, zeroed.Score);
        Assert.Equal(copied, zeroed.CollectionCopy);

        // Cópia abaixo do limite: a correção vale, e os trechos ficam gravados.
        var graded = Grade(Output(), Context(copied: copied));
        Assert.Equal(27, graded.Score);
        Assert.Equal("Texto 2", Assert.Single(graded.CollectionCopy).Label);
        Assert.Empty(Grade(Output()).CollectionCopy);
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
