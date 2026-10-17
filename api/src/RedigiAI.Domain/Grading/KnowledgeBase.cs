namespace RedigiAI.Domain.Grading;

// Camadas da base de conhecimento da correção (especificação, seção 8). Todas são versionadas e nunca
// editadas depois de criadas: editar é inserir uma versão nova e montar um perfil de correção novo com ela.

public enum PromptKind
{
    Transcription,
    Grading,
}

/// <summary>Camada 1 · Instruções da IA. Os trechos entre <c>{{ }}</c> são preenchidos pelo worker.</summary>
public sealed class PromptTemplate
{
    private PromptTemplate() { }

    public Guid Id { get; private set; }
    public PromptKind Kind { get; private set; }
    public int Version { get; private set; }
    public string SystemText { get; private set; } = null!;

    /// <summary>Modelo da mensagem do usuário (partes por tema e por redação), quando houver.</summary>
    public string? UserText { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static PromptTemplate Create(PromptKind kind, int version, string systemText, string? userText, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(systemText);

        return new PromptTemplate
        {
            Id = Guid.CreateVersion7(),
            Kind = kind,
            Version = version,
            SystemText = systemText,
            UserText = userText,
            CreatedAt = now,
        };
    }
}

/// <param name="CriterionPosition">Posição do critério na grade (1 a 4 na Fuvest).</param>
public sealed record CriterionGuideline(int CriterionPosition, string Content);

/// <summary>Camada 3 · Manual do corretor: regras gerais, uma entrada por critério e como escrever a orientação.</summary>
public sealed class GuidelinesVersion
{
    private readonly List<RubricGuideline> _criteria = [];

    private GuidelinesVersion() { }

    public Guid Id { get; private set; }
    public Guid ExamId { get; private set; }
    public int Version { get; private set; }
    public string GeneralRules { get; private set; } = null!;
    public string GuidanceWriting { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
    public IReadOnlyList<RubricGuideline> Criteria => _criteria;

    public static GuidelinesVersion Create(
        Guid examId,
        int version,
        string generalRules,
        string guidanceWriting,
        IReadOnlyList<CriterionGuideline> criteria,
        DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        if (criteria.Select(c => c.CriterionPosition).Distinct().Count() != criteria.Count)
        {
            throw new ArgumentException("O manual tem uma entrada por critério.", nameof(criteria));
        }

        var guidelines = new GuidelinesVersion
        {
            Id = Guid.CreateVersion7(),
            ExamId = examId,
            Version = version,
            GeneralRules = generalRules,
            GuidanceWriting = guidanceWriting,
            CreatedAt = now,
        };
        guidelines._criteria.AddRange(criteria.Select(c => RubricGuideline.Create(guidelines.Id, c)));
        return guidelines;
    }
}

public sealed class RubricGuideline
{
    private RubricGuideline() { }

    public Guid Id { get; private set; }
    public Guid GuidelinesVersionId { get; private set; }
    public int CriterionPosition { get; private set; }
    public string Content { get; private set; } = null!;

    internal static RubricGuideline Create(Guid guidelinesVersionId, CriterionGuideline guideline) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            GuidelinesVersionId = guidelinesVersionId,
            CriterionPosition = guideline.CriterionPosition,
            Content = guideline.Content,
        };
}

public sealed record WritingRuleSpec(string Code, string Name, string Explanation, string WrongExample, string RightExample);

/// <summary>Camada 4 · Catálogo dos desvios de escrita do critério de correção gramatical.</summary>
public sealed class WritingRulesVersion
{
    /// <summary>Código para desvio fora do catálogo; a IA descreve o que encontrou.</summary>
    public const string OtherCode = "OUTRO";

    private readonly List<WritingRule> _rules = [];

    private WritingRulesVersion() { }

    public Guid Id { get; private set; }
    public int Version { get; private set; }

    /// <summary>Observações que valem para o catálogo inteiro.</summary>
    public string? Notes { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public IReadOnlyList<WritingRule> Rules => _rules;

    public static WritingRulesVersion Create(int version, IReadOnlyList<WritingRuleSpec> rules, string? notes, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        if (rules.Select(r => r.Code).Distinct(StringComparer.Ordinal).Count() != rules.Count)
        {
            throw new ArgumentException("Os códigos das regras de escrita não se repetem.", nameof(rules));
        }

        var catalog = new WritingRulesVersion { Id = Guid.CreateVersion7(), Version = version, Notes = notes, CreatedAt = now };
        catalog._rules.AddRange(rules.Select((rule, index) => WritingRule.Create(catalog.Id, index + 1, rule)));
        return catalog;
    }
}

public sealed class WritingRule
{
    private WritingRule() { }

    public Guid Id { get; private set; }
    public Guid WritingRulesVersionId { get; private set; }
    public int Position { get; private set; }
    public string Code { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public string Explanation { get; private set; } = null!;
    public string WrongExample { get; private set; } = null!;
    public string RightExample { get; private set; } = null!;

    internal static WritingRule Create(Guid versionId, int position, WritingRuleSpec spec) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            WritingRulesVersionId = versionId,
            Position = position,
            Code = spec.Code,
            Name = spec.Name,
            Explanation = spec.Explanation,
            WrongExample = spec.WrongExample,
            RightExample = spec.RightExample,
        };
}

public enum GradingProfileStatus
{
    Draft,
    Active,
    Retired,
}

/// <summary>
/// Perfil de correção: as versões de cada camada e os modelos usados para um vestibular e tipo de redação.
/// Só guarda referências. Existe no máximo um ativo por vestibular e tipo; um perfil ativo nunca é editado,
/// só substituído.
/// </summary>
public sealed class GradingProfile
{
    private GradingProfile() { }

    public Guid Id { get; private set; }
    public Guid ExamId { get; private set; }
    public Guid EssayTypeId { get; private set; }
    public int Version { get; private set; }
    public GradingProfileStatus Status { get; private set; }
    public Guid TranscriptionPromptId { get; private set; }
    public Guid GradingPromptId { get; private set; }
    public Guid RubricVersionId { get; private set; }
    public Guid GuidelinesVersionId { get; private set; }
    public Guid WritingRulesVersionId { get; private set; }

    /// <summary>Redações-âncora; nulo enquanto não houver redações avaliadas por professor.</summary>
    public Guid? AnchorSetId { get; private set; }
    public string TranscriptionModel { get; private set; } = null!;
    public string GradingModel { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ActivatedAt { get; private set; }

    public static GradingProfile Draft(
        Guid examId,
        Guid essayTypeId,
        int version,
        PromptTemplate transcriptionPrompt,
        PromptTemplate gradingPrompt,
        Guid rubricVersionId,
        Guid guidelinesVersionId,
        Guid writingRulesVersionId,
        string transcriptionModel,
        string gradingModel,
        DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(transcriptionModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(gradingModel);
        if (transcriptionPrompt.Kind != PromptKind.Transcription || gradingPrompt.Kind != PromptKind.Grading)
        {
            throw new ArgumentException("Cada instrução precisa ser do tipo certo: transcrição e correção.");
        }

        return new GradingProfile
        {
            Id = Guid.CreateVersion7(),
            ExamId = examId,
            EssayTypeId = essayTypeId,
            Version = version,
            Status = GradingProfileStatus.Draft,
            TranscriptionPromptId = transcriptionPrompt.Id,
            GradingPromptId = gradingPrompt.Id,
            RubricVersionId = rubricVersionId,
            GuidelinesVersionId = guidelinesVersionId,
            WritingRulesVersionId = writingRulesVersionId,
            TranscriptionModel = transcriptionModel,
            GradingModel = gradingModel,
            CreatedAt = now,
        };
    }

    /// <summary>Passa a corrigir os alunos. O perfil ativo anterior precisa ser aposentado na mesma transação.</summary>
    public void Activate(DateTimeOffset now)
    {
        if (Status == GradingProfileStatus.Active)
        {
            return;
        }

        Status = GradingProfileStatus.Active;
        ActivatedAt = now;
    }

    public void Retire()
    {
        if (Status != GradingProfileStatus.Active)
        {
            throw new InvalidOperationException("Só um perfil ativo pode ser aposentado.");
        }

        Status = GradingProfileStatus.Retired;
    }
}

// Calibração: tabelas criadas agora e preenchidas quando houver redações avaliadas por professor.

/// <summary>Camada 5 · Conjunto de redações-âncora de um vestibular e tipo.</summary>
public sealed class AnchorSet
{
    private AnchorSet() { }

    public Guid Id { get; private set; }
    public Guid ExamId { get; private set; }
    public Guid EssayTypeId { get; private set; }
    public int Version { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}

/// <summary>Redação real avaliada por professor, uma por faixa de desempenho.</summary>
public sealed class AnchorEssay
{
    private AnchorEssay() { }

    public Guid Id { get; private set; }
    public Guid AnchorSetId { get; private set; }
    public string Band { get; private set; } = null!;
    public string Lines { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
}

public sealed class AnchorScore
{
    private AnchorScore() { }

    public Guid Id { get; private set; }
    public Guid AnchorEssayId { get; private set; }
    public int CriterionPosition { get; private set; }
    public int Level { get; private set; }
    public string Justification { get; private set; } = null!;
}

/// <summary>Redação do conjunto de referência, com os níveis dados por professor.</summary>
public sealed class ReferenceEssay
{
    private ReferenceEssay() { }

    public Guid Id { get; private set; }
    public Guid ExamId { get; private set; }
    public Guid EssayTypeId { get; private set; }
    public Guid? PromptId { get; private set; }
    public string Source { get; private set; } = null!;
    public string Lines { get; private set; } = null!;
    public string Levels { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
}

/// <summary>Execução de um perfil sobre o conjunto de referência: diferença por critério e custo.</summary>
public sealed class EvalRun
{
    private EvalRun() { }

    public Guid Id { get; private set; }
    public Guid GradingProfileId { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public string? Results { get; private set; }
    public long? CostMicroUsd { get; private set; }
    public bool? Passed { get; private set; }
}
