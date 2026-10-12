namespace RedigiAI.Domain.Catalog;

/// <param name="Description">Texto da hipótese, como está na grade.</param>
/// <param name="StudentNote">O que a regra significa, escrito para o aluno; aparece na correção com nota zero.</param>
public sealed record ZeroRule(string Code, string Description, string StudentNote = "");

/// <param name="ShortName">Nome curto do critério, como "Tema e autoria"; sem ele, vale o nome completo.</param>
public sealed record CriterionSpec(
    string Name, int Weight, IReadOnlyDictionary<int, string> LevelDescriptors, string? ShortName = null);

/// <summary>
/// Grade de correção. Imutável depois de publicada: editar é publicar uma versão nova.
/// O número de níveis é da versão; nenhum código pode supor quantos são.
/// </summary>
public sealed class RubricVersion
{
    public const int MinLevel = 1;

    private readonly List<RubricCriterion> _criteria = [];

    private RubricVersion() { }

    public Guid Id { get; private set; }
    public Guid ExamId { get; private set; }
    public int Version { get; private set; }
    public DateTimeOffset PublishedAt { get; private set; }

    /// <summary>Nível mais alto de cada critério nesta versão (os níveis vão de 1 até aqui).</summary>
    public int MaxLevel { get; private set; }

    /// <summary>Soma de nível máximo × peso de cada critério.</summary>
    public int MaxScore { get; private set; }

    /// <summary>De onde veio o texto da grade, como "Simulado Fuvest 2027 – 2ª edição".</summary>
    public string? Source { get; private set; }

    /// <summary>Como a grade é chamada para o aluno, como "Fuvest 2027"; a versão entra ao lado.</summary>
    public string? Label { get; private set; }
    public IReadOnlyList<ZeroRule> ZeroRules { get; private set; } = [];
    public IReadOnlyList<RubricCriterion> Criteria => _criteria;

    public static RubricVersion Publish(
        Guid examId,
        int version,
        int maxLevel,
        DateTimeOffset publishedAt,
        IReadOnlyList<ZeroRule> zeroRules,
        IReadOnlyList<CriterionSpec> criteria,
        string? source = null,
        string? label = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLevel, 2);
        if (criteria.Count == 0)
        {
            throw new ArgumentException("A grade precisa de ao menos um critério.", nameof(criteria));
        }

        var rubric = new RubricVersion
        {
            Id = Guid.CreateVersion7(),
            ExamId = examId,
            Version = version,
            MaxLevel = maxLevel,
            PublishedAt = publishedAt,
            Source = source,
            Label = label,
            ZeroRules = [.. zeroRules],
        };

        for (var i = 0; i < criteria.Count; i++)
        {
            rubric._criteria.Add(RubricCriterion.Create(rubric.Id, i + 1, maxLevel, criteria[i]));
        }

        rubric.MaxScore = rubric._criteria.Sum(c => c.MaxPoints);
        return rubric;
    }

    public bool HasLevel(int level) => level >= MinLevel && level <= MaxLevel;
}

public sealed class RubricCriterion
{
    private RubricCriterion() { }

    public Guid Id { get; private set; }
    public Guid RubricVersionId { get; private set; }
    public int Position { get; private set; }
    public string Name { get; private set; } = null!;

    /// <summary>Nome curto, usado nos títulos e filtros da correção.</summary>
    public string ShortName { get; private set; } = null!;
    public int Weight { get; private set; }

    /// <summary>Nível máximo da versão × peso.</summary>
    public int MaxPoints { get; private set; }
    public IReadOnlyDictionary<int, string> LevelDescriptors { get; private set; } = new Dictionary<int, string>();

    /// <summary>A nota do critério é sempre esta conta, feita pelo backend (regra 6).</summary>
    public int PointsFor(int level) => level * Weight;

    /// <summary>Pontos que faltam para o nível máximo: é também o quanto o critério ainda pode render.</summary>
    public int PointsLostAt(int level) => MaxPoints - PointsFor(level);

    internal static RubricCriterion Create(Guid rubricVersionId, int position, int maxLevel, CriterionSpec spec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.Name);
        ArgumentOutOfRangeException.ThrowIfLessThan(spec.Weight, 1);

        var levels = Enumerable.Range(RubricVersion.MinLevel, maxLevel);
        if (spec.LevelDescriptors.Count != maxLevel || !levels.All(spec.LevelDescriptors.ContainsKey))
        {
            throw new ArgumentException(
                $"O critério '{spec.Name}' precisa de um descritor para cada nível de 1 a {maxLevel}.", nameof(spec));
        }

        return new RubricCriterion
        {
            Id = Guid.CreateVersion7(),
            RubricVersionId = rubricVersionId,
            Position = position,
            Name = spec.Name,
            ShortName = string.IsNullOrWhiteSpace(spec.ShortName) ? spec.Name : spec.ShortName,
            Weight = spec.Weight,
            MaxPoints = maxLevel * spec.Weight,
            LevelDescriptors = new Dictionary<int, string>(spec.LevelDescriptors),
        };
    }
}
