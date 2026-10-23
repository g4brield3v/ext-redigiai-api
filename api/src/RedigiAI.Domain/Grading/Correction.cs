using RedigiAI.Domain.Catalog;

namespace RedigiAI.Domain.Grading;

public enum MarkKind
{
    Good,
    Issue,
}

// O que a IA devolveu pela ferramenta, já lido do JSON. Nada aqui traz nota: ela é calculada pelo backend.

public sealed record GradedZero(bool Applies, string? Reason, string? Explanation);

/// <param name="PlainExplanation">O que muda do nível atual para o próximo neste texto, em palavras simples.</param>
/// <param name="Example">Como ficaria um trecho do texto, nos critérios 1 a 3.</param>
public sealed record GradedCriterion(
    int Position,
    int Level,
    string Justification,
    IReadOnlyList<int> Lines,
    string? Guidance,
    string? PlainExplanation = null,
    CriterionExample? Example = null);

/// <summary>Os valores são gravados como número no jsonb de <c>correction_criteria.example</c>: não mude nenhum.</summary>
public enum ExampleSegmentKind
{
    Keep = 0,
    Remove = 1,
    Add = 2,
}

/// <summary>Um pedaço do exemplo: texto do aluno mantido, texto do aluno tirado ou texto acrescentado.</summary>
public sealed record ExampleSegment(string Text, ExampleSegmentKind Kind);

/// <summary>Um trecho da redação reescrito como exemplo, não como a única forma.</summary>
/// <param name="LineLabel">Onde fica o trecho, como "Fim do parágrafo, linha 29".</param>
public sealed record CriterionExample(string LineLabel, IReadOnlyList<ExampleSegment> Segments);

/// <summary>Como ler a resposta de um apontamento.</summary>
public enum AnswerKind
{
    /// <summary>A forma correta, no critério de correção gramatical.</summary>
    Exact,

    /// <summary>Uma reescrita possível, nos outros critérios.</summary>
    Example,
}

/// <param name="Start">Posição no texto da linha: início incluído, fim excluído.</param>
/// <param name="Rule">Código da regra de escrita, nas marcas do critério de correção gramatical.</param>
/// <param name="Why">A regra geral por trás do apontamento, que vale para outros textos.</param>
/// <param name="Answer">A forma correta (critério 4) ou uma reescrita possível (critérios 1 a 3).</param>
/// <param name="AskLabel">O pedido ao aluno antes de ver a resposta, como "Escreva a frase que fecha o parágrafo".</param>
public sealed record GradedMark(
    int Criterion,
    MarkKind Kind,
    int Line,
    int Start,
    int End,
    string? Note,
    string? Rule,
    string? Why = null,
    string? Answer = null,
    string? AskLabel = null);

/// <summary>O problema principal ou um próximo passo, com o critério a que se refere.</summary>
/// <param name="Criterion">Posição do critério na grade; nulo quando o texto não é de um critério só.</param>
public sealed record GradedPoint(string Text, int? Criterion);

public sealed record GradingOutput(
    GradedZero Zero,
    IReadOnlyList<GradedCriterion> Criteria,
    IReadOnlyList<GradedMark> Marks,
    string Strengths,
    GradedPoint MainIssue,
    IReadOnlyList<GradedPoint> NextSteps);

/// <summary>Próximo passo da correção.</summary>
/// <param name="Criterion">Posição do critério a que o passo se refere; nulo com nota zero ou em passo geral.</param>
public sealed record NextStep(string Text, int? Criterion);

/// <summary>Linha da redação como foi para a correção: a transcrição com as palavras que o aluno digitou.</summary>
public sealed record EssayLine(int Number, string Text, bool Indent);

/// <summary>Um trecho da redação igual a um texto da coletânea, achado pela verificação automática.</summary>
/// <param name="Label">O texto da coletânea de onde veio ("Texto 2").</param>
/// <param name="Words">Quantas palavras seguidas são iguais.</param>
/// <param name="Text">O trecho como o aluno escreveu.</param>
public sealed record CopiedPassage(string Label, int LineStart, int LineEnd, int Words, string Text);

/// <summary>Com que a correção foi feita: tudo o que é preciso para explicar e reproduzir a nota depois.</summary>
/// <param name="PromptId">A versão da proposta usada.</param>
/// <param name="PromptGradingNotesId">As abordagens esperadas dessa versão.</param>
/// <param name="IdentificationFound">A transcrição encontrou nome, assinatura ou outra identificação na folha.</param>
/// <param name="WritingRuleCodes">Códigos do catálogo de regras de escrita do perfil.</param>
/// <param name="CopiedPassages">Trechos copiados da coletânea que a verificação automática encontrou.</param>
/// <param name="CopyRequiresZero">A cópia passa do limite: a correção só é aceita com nota zero.</param>
/// <param name="LearningFieldsRequired">
/// As instruções do perfil pedem a regra geral e a resposta de cada desvio. Um perfil ainda com as instruções
/// anteriores não é recusado por não mandá-las.
/// </param>
public sealed record GradingContext(
    Guid EssayId,
    RubricVersion Rubric,
    Guid GradingProfileId,
    Guid PromptId,
    Guid PromptGradingNotesId,
    IReadOnlyList<EssayLine> Lines,
    bool IdentificationFound,
    IReadOnlySet<string> WritingRuleCodes,
    IReadOnlyList<CopiedPassage> CopiedPassages,
    bool CopyRequiresZero,
    bool LearningFieldsRequired = true);

/// <summary>A resposta da IA não passa em uma das regras de validação; vale pedir de novo.</summary>
/// <param name="rule">Número da regra da seção 8 da especificação (1 a 11).</param>
/// <param name="code">Código da recusa, nas regras que têm um (<see cref="GradingRejections"/>).</param>
public sealed class InvalidGradingException(int rule, string reason, string? code = null) : Exception(reason)
{
    public int Rule { get; } = rule;

    public string? Code { get; } = code;
}

/// <summary>Códigos das recusas dos campos de aprendizado (regras 9 a 11 da validação).</summary>
public static class GradingRejections
{
    /// <summary>Um desvio sem a regra geral ou sem a resposta.</summary>
    public const string MissingField = "AI_MISSING_FIELD";

    /// <summary>O comentário ou a regra geral entrega a resposta que o aluno deveria tentar sozinho.</summary>
    public const string AnswerLeaked = "AI_ANSWER_LEAKED";

    /// <summary>O exemplo de reescrita parte de um texto que não está na redação.</summary>
    public const string ExampleMismatch = "AI_EXAMPLE_MISMATCH";
}

/// <summary>
/// Correção de uma redação. Só nasce de uma resposta da IA que passa nas regras de validação, e a nota é
/// sempre a conta do backend: soma de nível × peso, ou zero quando uma hipótese de nota zero se aplica.
/// </summary>
public sealed class Correction
{
    public const int MaxNextSteps = 3;

    /// <summary>
    /// Teto só contra o caso absurdo (redação feita para gerar marcas sem fim): uma redação real com muitos desvios
    /// fica bem abaixo disso. Passou, a correção fica com as marcas que tiraram pontos antes dos acertos.
    /// </summary>
    public const int MaxMarks = 80;

    /// <summary>
    /// O critério de correção gramatical da grade da Fuvest: nele a resposta é a forma correta exata e não há
    /// exemplo de reescrita.
    /// </summary>
    public const int WritingCriterion = 4;

    /// <summary>Tamanho máximo do pedido ao aluno antes da resposta.</summary>
    public const int MaxAskLabelLength = 60;

    /// <summary>
    /// Teto da regra geral, da resposta, da explicação e de cada pedaço do exemplo: bem acima de 3 frases, só
    /// para um campo não tomar a saída inteira.
    /// </summary>
    public const int MaxLearningTextLength = 800;

    /// <summary>Teto de pedaços de um exemplo de reescrita.</summary>
    public const int MaxExampleSegments = 20;

    private readonly List<CorrectionCriterion> _criteria = [];
    private readonly List<CorrectionMark> _marks = [];

    private Correction() { }

    public Guid Id { get; private set; }
    public Guid EssayId { get; private set; }
    public Guid RubricVersionId { get; private set; }
    public Guid GradingProfileId { get; private set; }
    public Guid PromptId { get; private set; }
    public Guid PromptGradingNotesId { get; private set; }

    /// <summary>Trechos copiados da coletânea que a verificação automática encontrou e informou à IA.</summary>
    public IReadOnlyList<CopiedPassage> CollectionCopy { get; private set; } = [];

    /// <summary>Código da hipótese de nota zero aplicada; nulo quando os critérios foram pontuados.</summary>
    public string? ZeroReason { get; private set; }
    public string? ZeroExplanation { get; private set; }
    public int Score { get; private set; }
    public int MaxScore { get; private set; }
    public string Strengths { get; private set; } = null!;
    public string MainIssue { get; private set; } = null!;

    /// <summary>Posição do critério do problema principal; nulo quando a nota é zero.</summary>
    public int? MainIssueCriterion { get; private set; }
    public IReadOnlyList<NextStep> NextSteps { get; private set; } = [];
    public string Model { get; private set; } = null!;

    /// <summary>O que a IA devolveu, como veio, para auditoria.</summary>
    public string RawResponse { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
    public IReadOnlyList<CorrectionCriterion> Criteria => _criteria;
    public IReadOnlyList<CorrectionMark> Marks => _marks;

    /// <exception cref="InvalidGradingException">A resposta não passa nas regras 2 a 8 da validação.</exception>
    public static Correction Grade(
        GradingContext context, GradingOutput output, string model, string rawResponse, DateTimeOffset now)
    {
        var rubric = context.Rubric;
        var zero = output.Zero.Applies;

        if (zero && !rubric.ZeroRules.Any(rule => rule.Code == output.Zero.Reason))
        {
            throw new InvalidGradingException(6, "Zero reason is not one of the rubric's zero rules.");
        }

        if (context.IdentificationFound && !zero)
        {
            throw new InvalidGradingException(7, "The sheet has an identification but the grading did not apply zero.");
        }

        if (context.CopyRequiresZero && !zero)
        {
            throw new InvalidGradingException(8, "Most of the essay was copied from the collection but the grading did not apply zero.");
        }

        // A IA marca todos os trechos que afetam a nota (docs/08-ajustes-correcao.md, item 1); o teto de marcas só
        // corta o caso absurdo, sem recusar a resposta (ver AddMarks). Os 3 próximos passos são limite pedagógico.
        if (output.NextSteps.Count > MaxNextSteps)
        {
            throw new InvalidGradingException(5, "More than 3 next steps.");
        }

        if (output.NextSteps.Any(step => Feedback.IsQuestion(step.Text)))
        {
            throw new InvalidGradingException(4, "A next step is a question.");
        }

        // O problema principal é o destaque da correção ("Comece por aqui"): também é sempre afirmação.
        if (Feedback.IsQuestion(output.MainIssue.Text))
        {
            throw new InvalidGradingException(4, "The main issue is a question.");
        }

        // Com nota zero nenhum critério é pontuado: o problema principal e os passos não ficam ligados a critério.
        if (!zero && (output.MainIssue.Criterion is not { } mainCriterion || !HasCriterion(rubric, mainCriterion)))
        {
            throw new InvalidGradingException(2, "The main issue does not point to a criterion of the rubric.");
        }

        if (!zero && output.NextSteps.Any(step => step.Criterion is { } position && !HasCriterion(rubric, position)))
        {
            throw new InvalidGradingException(2, "A next step points to a criterion that is not in the rubric.");
        }

        var correction = new Correction
        {
            Id = Guid.CreateVersion7(),
            EssayId = context.EssayId,
            RubricVersionId = rubric.Id,
            GradingProfileId = context.GradingProfileId,
            PromptId = context.PromptId,
            PromptGradingNotesId = context.PromptGradingNotesId,
            CollectionCopy = context.CopiedPassages,
            ZeroReason = zero ? output.Zero.Reason : null,
            ZeroExplanation = zero ? output.Zero.Explanation : null,
            MaxScore = rubric.MaxScore,
            Strengths = output.Strengths,
            MainIssue = output.MainIssue.Text,
            MainIssueCriterion = zero ? null : output.MainIssue.Criterion,
            NextSteps = [.. output.NextSteps.Select(step => new NextStep(step.Text, zero ? null : step.Criterion))],
            Model = model,
            RawResponse = rawResponse,
            CreatedAt = now,
        };

        var textByLine = context.Lines.ToDictionary(line => line.Number, line => line.Text);

        // Com nota zero os critérios não são pontuados: o que a IA mandar para eles é ignorado.
        if (!zero)
        {
            correction.AddCriteria(rubric, output.Criteria, textByLine);

            // Por onde começar tem de render pontos: só vale um critério no nível máximo quando todos estão nele.
            if (correction._criteria.Single(c => c.Position == output.MainIssue.Criterion).Level == rubric.MaxLevel
                && correction._criteria.Any(c => c.Level < rubric.MaxLevel))
            {
                throw new InvalidGradingException(2, "The main issue points to a criterion that is already at the top level.");
            }
        }

        correction.AddMarks(rubric, output.Marks, textByLine, context.WritingRuleCodes, context.LearningFieldsRequired);
        correction.Score = correction._criteria.Sum(criterion => criterion.Points);
        return correction;
    }

    /// <summary>
    /// Regras 4, 9 e 10 nos campos de aprendizado de um desvio: a regra geral e a resposta são obrigatórias, nada
    /// é pergunta, e nem o comentário nem a regra geral entregam a resposta. Num acerto, os campos são descartados.
    /// Com as instruções anteriores do perfil, que não pedem esses campos, a falta deles não recusa a resposta.
    /// </summary>
    /// <param name="quote">O trecho marcado: a resposta pode ter "?" se o trecho do aluno já é uma pergunta.</param>
    private static GradedMark LearningFields(GradedMark mark, string quote, bool required)
    {
        if (mark.Kind != MarkKind.Issue)
        {
            return mark with { Why = null, Answer = null, AskLabel = null };
        }

        var why = string.IsNullOrWhiteSpace(mark.Why) ? null : mark.Why;
        var answer = string.IsNullOrWhiteSpace(mark.Answer) ? null : mark.Answer;
        var askLabel = string.IsNullOrWhiteSpace(mark.AskLabel) ? null : mark.AskLabel.Trim();
        if (required && (why is null || answer is null))
        {
            throw new InvalidGradingException(9, "An issue mark has no why or no answer.", GradingRejections.MissingField);
        }

        if ((why is not null && Feedback.IsQuestion(why))
            || (answer?.Contains('?') == true && !quote.Contains('?'))
            || askLabel?.Contains('?') == true)
        {
            throw new InvalidGradingException(4, "The why, the answer or the ask label of a mark is a question.");
        }

        if (askLabel?.Length > MaxAskLabelLength || why?.Length > MaxLearningTextLength || answer?.Length > MaxLearningTextLength)
        {
            throw new InvalidGradingException(1, "A learning field of a mark is too long.");
        }

        if (answer is not null && (LearningText.Reveals(mark.Note, answer) || LearningText.Reveals(why, answer)))
        {
            throw new InvalidGradingException(10, "The note or the why of a mark gives the answer.", GradingRejections.AnswerLeaked);
        }

        return mark with { Why = why, Answer = answer, AskLabel = askLabel };
    }

    private static bool HasCriterion(RubricVersion rubric, int position) =>
        rubric.Criteria.Any(criterion => criterion.Position == position);

    private void AddCriteria(
        RubricVersion rubric, IReadOnlyList<GradedCriterion> graded, Dictionary<int, string> textByLine)
    {
        if (graded.Count != rubric.Criteria.Count
            || !rubric.Criteria.All(criterion => graded.Count(item => item.Position == criterion.Position) == 1))
        {
            throw new InvalidGradingException(2, "The grading does not have exactly one item per criterion.");
        }

        foreach (var criterion in rubric.Criteria.OrderBy(c => c.Position))
        {
            var item = graded.Single(g => g.Position == criterion.Position);
            if (!rubric.HasLevel(item.Level))
            {
                throw new InvalidGradingException(2, $"Criterion {criterion.Position} has a level outside the rubric.");
            }

            if (item.Lines.Any(line => !textByLine.ContainsKey(line)))
            {
                throw new InvalidGradingException(3, $"Criterion {criterion.Position} cites a line that is not in the essay.");
            }

            if (item.Guidance is { } guidance && Feedback.IsQuestion(guidance))
            {
                throw new InvalidGradingException(4, $"The guidance of criterion {criterion.Position} is a question.");
            }

            // Explicação e exemplo só servem a quem ainda pode subir de nível; o critério 4 não tem exemplo.
            var belowTop = item.Level < rubric.MaxLevel;
            var explanation = belowTop && !string.IsNullOrWhiteSpace(item.PlainExplanation) ? item.PlainExplanation : null;
            var example = belowTop && criterion.Position != WritingCriterion ? item.Example : null;

            if (explanation is not null && Feedback.IsQuestion(explanation))
            {
                throw new InvalidGradingException(4, $"The plain explanation of criterion {criterion.Position} is a question.");
            }

            if (explanation?.Length > MaxLearningTextLength
                || (example is not null
                    && (example.Segments.Count > MaxExampleSegments
                        || example.LineLabel.Length > MaxAskLabelLength
                        || example.Segments.Any(segment => segment.Text.Length > MaxLearningTextLength))))
            {
                throw new InvalidGradingException(1, $"A learning field of criterion {criterion.Position} is too long.");
            }

            if (example is not null && !LearningText.ExampleMatches(example, textByLine))
            {
                throw new InvalidGradingException(
                    11, $"The example of criterion {criterion.Position} is not in the essay.", GradingRejections.ExampleMismatch);
            }

            _criteria.Add(new CorrectionCriterion(Id, criterion, item with { PlainExplanation = explanation, Example = example }));
        }
    }

    private void AddMarks(
        RubricVersion rubric,
        IReadOnlyList<GradedMark> marks,
        Dictionary<int, string> textByLine,
        IReadOnlySet<string> ruleCodes,
        bool learningFieldsRequired)
    {
        // Toda marca é validada, mesmo as que o teto vai cortar: uma posição inventada continua recusando a resposta.
        var valid = new List<(RubricCriterion Criterion, GradedMark Mark)>(marks.Count);
        foreach (var mark in marks)
        {
            var criterion = rubric.Criteria.FirstOrDefault(c => c.Position == mark.Criterion)
                ?? throw new InvalidGradingException(2, "A mark points to a criterion that is not in the rubric.");

            if (!textByLine.TryGetValue(mark.Line, out var text)
                || mark.Start < 0 || mark.End <= mark.Start || mark.End > text.Length)
            {
                throw new InvalidGradingException(3, "A mark cites a line or a position that is not in the essay.");
            }

            // Regra fora do catálogo vira "outro desvio": o que a IA escreveu ali nunca é gravado como código.
            var rule = string.IsNullOrWhiteSpace(mark.Rule)
                ? null
                : ruleCodes.Contains(mark.Rule.Trim()) ? mark.Rule.Trim() : WritingRulesVersion.OtherCode;
            valid.Add((criterion, LearningFields(mark, text[mark.Start..mark.End], learningFieldsRequired) with { Rule = rule }));
        }

        // Marca repetida (mesmo critério, tipo e trecho) entra uma vez só. Acima do teto, a correção não é recusada:
        // recusar devolveria a correção ao aluno, que poderia reenviar o mesmo texto sem fim. Ficam primeiro as
        // marcas que tiraram pontos, na ordem em que vieram, e a lista volta para a ordem da IA.
        var kept = valid
            .DistinctBy(item => (item.Mark.Criterion, item.Mark.Kind, item.Mark.Line, item.Mark.Start, item.Mark.End))
            .Select((item, index) => (item, index))
            .OrderBy(entry => entry.item.Mark.Kind == MarkKind.Issue ? 0 : 1)
            .ThenBy(entry => entry.index)
            .Take(MaxMarks)
            .OrderBy(entry => entry.index);

        foreach (var (item, _) in kept)
        {
            _marks.Add(new CorrectionMark(Id, item.Criterion, item.Mark));
        }
    }
}

public sealed class CorrectionCriterion
{
    private CorrectionCriterion() { }

    internal CorrectionCriterion(Guid correctionId, RubricCriterion criterion, GradedCriterion graded)
    {
        Id = Guid.CreateVersion7();
        CorrectionId = correctionId;
        CriterionId = criterion.Id;
        Position = criterion.Position;
        Level = graded.Level;
        Points = criterion.PointsFor(graded.Level);
        Justification = graded.Justification;
        Guidance = string.IsNullOrWhiteSpace(graded.Guidance) ? null : graded.Guidance;
        Lines = [.. graded.Lines.Distinct().Order()];
        PlainExplanation = graded.PlainExplanation;
        Example = graded.Example;
    }

    public Guid Id { get; private set; }
    public Guid CorrectionId { get; private set; }
    public Guid CriterionId { get; private set; }
    public int Position { get; private set; }
    public int Level { get; private set; }

    /// <summary>Nível × peso, calculado pelo backend.</summary>
    public int Points { get; private set; }
    public string Justification { get; private set; } = null!;

    /// <summary>O que corrigir, sempre como afirmação; nulo quando o nível é o máximo.</summary>
    public string? Guidance { get; private set; }
    public int[] Lines { get; private set; } = [];

    /// <summary>O que muda para o próximo nível neste texto; nulo no nível máximo e nas correções antigas.</summary>
    public string? PlainExplanation { get; private set; }

    /// <summary>Um trecho reescrito, nos critérios 1 a 3 abaixo do nível máximo; opcional.</summary>
    public CriterionExample? Example { get; private set; }
}

public sealed class CorrectionMark
{
    private CorrectionMark() { }

    internal CorrectionMark(Guid correctionId, RubricCriterion criterion, GradedMark mark)
    {
        Id = Guid.CreateVersion7();
        CorrectionId = correctionId;
        CriterionId = criterion.Id;
        CriterionPosition = criterion.Position;
        Kind = mark.Kind;
        LineStart = mark.Line;
        LineEnd = mark.Line;
        CharStart = mark.Start;
        CharEnd = mark.End;
        Note = string.IsNullOrWhiteSpace(mark.Note) ? null : mark.Note;
        RuleCode = string.IsNullOrWhiteSpace(mark.Rule) ? null : mark.Rule;

        // Os campos de aprendizado só existem nos desvios: a validação já descartou os dos acertos.
        Why = mark.Why;
        Answer = mark.Answer;
        AskLabel = mark.AskLabel;
        AnswerKind = Answer is null
            ? null
            : criterion.Position == Correction.WritingCriterion ? Grading.AnswerKind.Exact : Grading.AnswerKind.Example;

        // A forma correta continua no contrato antigo: é a resposta, quando ela é exata.
        Fix = AnswerKind == Grading.AnswerKind.Exact ? Answer : null;
    }

    public Guid Id { get; private set; }
    public Guid CorrectionId { get; private set; }
    public Guid CriterionId { get; private set; }
    public int CriterionPosition { get; private set; }
    public MarkKind Kind { get; private set; }
    public int LineStart { get; private set; }

    /// <summary>Igual à linha inicial: a IA marca um trecho dentro de uma linha.</summary>
    public int LineEnd { get; private set; }
    public int CharStart { get; private set; }
    public int CharEnd { get; private set; }

    /// <summary>Comentário do apontamento.</summary>
    public string? Note { get; private set; }

    /// <summary>Código da regra de escrita (<c>writing_rules</c>), quando a marca é um desvio de escrita.</summary>
    public string? RuleCode { get; private set; }

    /// <summary>A forma correta do trecho, nos desvios do critério 4: o mesmo valor da resposta exata.</summary>
    public string? Fix { get; private set; }

    /// <summary>A regra geral por trás do desvio; nulo nos acertos e nas correções antigas.</summary>
    public string? Why { get; private set; }

    /// <summary>A forma correta (critério 4) ou uma reescrita possível (critérios 1 a 3).</summary>
    public string? Answer { get; private set; }

    public AnswerKind? AnswerKind { get; private set; }

    /// <summary>O pedido ao aluno antes de ver a resposta; opcional.</summary>
    public string? AskLabel { get; private set; }
}

public enum FeedbackRating
{
    HelpedALot,
    HelpedPartly,
    DidNotHelp,
}

/// <summary>Avaliação que o aluno faz da correção: uma por correção.</summary>
public sealed class CorrectionFeedback
{
    public const int MaxCommentLength = 1000;

    private CorrectionFeedback() { }

    public Guid Id { get; private set; }
    public Guid CorrectionId { get; private set; }
    public FeedbackRating Rating { get; private set; }
    public string? Comment { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>O comentário cabe no limite e só tem texto: nenhum caractere de controle além de quebra de linha e tabulação.</summary>
    public static bool AcceptsComment(string? comment) =>
        comment is null
        || (comment.Trim().Length <= MaxCommentLength
            && !comment.Any(character => char.IsControl(character) && character is not ('\n' or '\r' or '\t')));

    /// <exception cref="ArgumentException">Comentário acima do limite ou com caractere de controle.</exception>
    public static CorrectionFeedback Create(Guid correctionId, FeedbackRating rating, string? comment, DateTimeOffset now)
    {
        if (!AcceptsComment(comment))
        {
            throw new ArgumentException("Comment is too long or has control characters.", nameof(comment));
        }

        var text = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();

        return new CorrectionFeedback
        {
            Id = Guid.CreateVersion7(),
            CorrectionId = correctionId,
            Rating = rating,
            Comment = text,
            CreatedAt = now,
        };
    }
}

/// <summary>Feedback é sempre afirmação, nunca pergunta (regra 7 do produto).</summary>
public static class Feedback
{
    private static readonly (char Open, char Close)[] Quotes = [('"', '"'), ('“', '”'), ('«', '»')];
    private static readonly char[] Trailing = ['"', '”', '»', '\'', '’', ')', '.', ' ', '\n', '\r', '\t'];

    /// <summary>
    /// Verdadeiro quando o texto termina com "?" ou traz "?" fora de um trecho entre aspas. Um "?" dentro de
    /// aspas é citação do texto do aluno e só é recusado se o texto terminar nele.
    /// </summary>
    public static bool IsQuestion(string text)
    {
        if (text.TrimEnd(Trailing).EndsWith('?'))
        {
            return true;
        }

        char? closing = null;
        var insideOpenQuote = false;
        foreach (var character in text)
        {
            if (closing is not null)
            {
                if (character == closing)
                {
                    closing = null;
                    insideOpenQuote = false;
                }
                else if (character == '?')
                {
                    insideOpenQuote = true;
                }

                continue;
            }

            if (character == '?')
            {
                return true;
            }

            foreach (var (open, close) in Quotes)
            {
                if (character == open)
                {
                    closing = close;
                }
            }
        }

        // Aspas que não fecham não protegem a pergunta.
        return insideOpenQuote;
    }
}

/// <summary>Comparações de texto dos campos de aprendizado.</summary>
public static class LearningText
{
    /// <summary>
    /// Verdadeiro quando o texto traz a resposta, palavra por palavra, sem contar maiúsculas, aspas e pontuação.
    /// Resposta de uma palavra só, com até 3 letras ("à", "há"), não conta: ela aparece por acaso em qualquer
    /// explicação.
    /// </summary>
    public static bool Reveals(string? text, string answer)
    {
        var answerWords = Words(answer);
        if (text is null || answerWords.Count == 0 || (answerWords.Count == 1 && answerWords[0].Length <= 3))
        {
            return false;
        }

        var words = Words(text);
        for (var start = 0; start + answerWords.Count <= words.Count; start++)
        {
            if (words.Skip(start).Take(answerWords.Count).SequenceEqual(answerWords, StringComparer.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Verdadeiro quando o texto do aluno no exemplo (os pedaços mantidos e tirados, na ordem) está na redação.
    /// Espaços e quebras de linha não contam; a palavra partida por hífen no fim da linha vale partida ou inteira.
    /// </summary>
    public static bool ExampleMatches(CriterionExample example, IReadOnlyDictionary<int, string> textByLine)
    {
        var original = Compact(string.Concat(example.Segments
            .Where(segment => segment.Kind != ExampleSegmentKind.Add)
            .Select(segment => segment.Text)));
        if (original.Length == 0)
        {
            return false;
        }

        var lines = textByLine.OrderBy(line => line.Key).Select(line => line.Value.TrimEnd()).ToList();
        var asWritten = Compact(string.Concat(lines));
        var joined = Compact(string.Concat(lines.Select(line => line.EndsWith('-') ? line[..^1] : line)));
        return asWritten.Contains(original, StringComparison.Ordinal) || joined.Contains(original, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sem espaços, e com os sinais de menor e maior da forma como a IA os leu (o texto do aluno vai para ela com
    /// "＜" e "＞", para não fechar a marcação): o exemplo copiado do que ela viu também bate.
    /// </summary>
    private static string Compact(string text) =>
        string.Concat(text.Where(character => !char.IsWhiteSpace(character))).Replace('＜', '<').Replace('＞', '>');

    /// <summary>As palavras do texto em minúsculas; aspas, pontuação e espaços só separam.</summary>
    private static List<string> Words(string text)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var character in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                current.Append(character);
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }

        return words;
    }
}

public interface ICorrectionRepository
{
    void Add(Correction correction);

    Task<bool> ExistsAsync(Guid essayId, CancellationToken ct);

    /// <summary>A correção da redação, com critérios e marcas; <c>null</c> se ainda não existe.</summary>
    Task<Correction?> FindByEssayAsync(Guid essayId, CancellationToken ct);

    /// <summary>A correção pelo id, sem critérios nem marcas; <c>null</c> se não existe.</summary>
    Task<Correction?> FindAsync(Guid id, CancellationToken ct);

    void AddFeedback(CorrectionFeedback feedback);

    Task<CorrectionFeedback?> FindFeedbackAsync(Guid correctionId, CancellationToken ct);

    /// <summary>Nota e nota máxima de cada redação já corrigida.</summary>
    Task<IReadOnlyDictionary<Guid, (int Score, int MaxScore)>> ScoresAsync(
        IReadOnlyCollection<Guid> essayIds, CancellationToken ct);

    /// <summary>As notas das correções mais recentes do aluno, da mais recente para a mais antiga.</summary>
    Task<IReadOnlyList<ProgressScore>> RecentScoresAsync(Guid userId, int count, CancellationToken ct);

    /// <summary>
    /// As correções sem nota zero mais recentes do aluno, da mais recente para a mais antiga, com os critérios e os
    /// pontos que cada um perdeu.
    /// </summary>
    Task<IReadOnlyList<ProgressCorrection>> RecentGradedAsync(Guid userId, int count, CancellationToken ct);
}
