using RedigiAI.Domain.Catalog;

namespace RedigiAI.Domain.Grading;

public enum MarkKind
{
    Good,
    Issue,
}

// O que a IA devolveu pela ferramenta, já lido do JSON. Nada aqui traz nota: ela é calculada pelo backend.

public sealed record GradedZero(bool Applies, string? Reason, string? Explanation);

public sealed record GradedCriterion(int Position, int Level, string Justification, IReadOnlyList<int> Lines, string? Guidance);

/// <param name="Start">Posição no texto da linha: início incluído, fim excluído.</param>
/// <param name="Rule">Código da regra de escrita, nas marcas do critério de correção gramatical.</param>
/// <param name="Fix">A forma correta do trecho, nos desvios de escrita.</param>
public sealed record GradedMark(
    int Criterion, MarkKind Kind, int Line, int Start, int End, string? Note, string? Rule, string? Fix = null);

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

/// <summary>Com que a correção foi feita: tudo o que é preciso para explicar e reproduzir a nota depois.</summary>
/// <param name="PromptId">A versão da proposta usada.</param>
/// <param name="PromptGradingNotesId">As abordagens esperadas dessa versão.</param>
/// <param name="IdentificationFound">A transcrição encontrou nome, assinatura ou outra identificação na folha.</param>
/// <param name="WritingRuleCodes">Códigos do catálogo de regras de escrita do perfil.</param>
public sealed record GradingContext(
    Guid EssayId,
    RubricVersion Rubric,
    Guid GradingProfileId,
    Guid PromptId,
    Guid PromptGradingNotesId,
    IReadOnlyList<EssayLine> Lines,
    bool IdentificationFound,
    IReadOnlySet<string> WritingRuleCodes);

/// <summary>A resposta da IA não passa em uma das regras de validação; vale pedir de novo.</summary>
/// <param name="rule">Número da regra da seção 8 da especificação (1 a 7).</param>
public sealed class InvalidGradingException(int rule, string reason) : Exception(reason)
{
    public int Rule { get; } = rule;
}

/// <summary>
/// Correção de uma redação. Só nasce de uma resposta da IA que passa nas regras de validação, e a nota é
/// sempre a conta do backend: soma de nível × peso, ou zero quando uma hipótese de nota zero se aplica.
/// </summary>
public sealed class Correction
{
    public const int MaxNextSteps = 3;
    public const int MaxMarks = 12;

    private readonly List<CorrectionCriterion> _criteria = [];
    private readonly List<CorrectionMark> _marks = [];

    private Correction() { }

    public Guid Id { get; private set; }
    public Guid EssayId { get; private set; }
    public Guid RubricVersionId { get; private set; }
    public Guid GradingProfileId { get; private set; }
    public Guid PromptId { get; private set; }
    public Guid PromptGradingNotesId { get; private set; }

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

    /// <exception cref="InvalidGradingException">A resposta não passa nas regras 2 a 7 da validação.</exception>
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

        if (output.Marks.Count > MaxMarks)
        {
            throw new InvalidGradingException(1, "More than 12 marks.");
        }

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

        correction.AddMarks(rubric, output.Marks, textByLine, context.WritingRuleCodes);
        correction.Score = correction._criteria.Sum(criterion => criterion.Points);
        return correction;
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

            _criteria.Add(new CorrectionCriterion(Id, criterion, item));
        }
    }

    private void AddMarks(
        RubricVersion rubric,
        IReadOnlyList<GradedMark> marks,
        Dictionary<int, string> textByLine,
        IReadOnlySet<string> ruleCodes)
    {
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
            _marks.Add(new CorrectionMark(Id, criterion, mark with { Rule = rule }));
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

        // A forma correta só existe para desvio de escrita; fora disso o que a IA mandar é descartado.
        Fix = Kind == MarkKind.Issue && RuleCode is not null && !string.IsNullOrWhiteSpace(mark.Fix) ? mark.Fix : null;
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

    /// <summary>A forma correta do trecho, quando a marca é um desvio de escrita.</summary>
    public string? Fix { get; private set; }
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
}
