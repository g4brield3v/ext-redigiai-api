using RedigiAI.Domain.Common;

namespace RedigiAI.Domain.Catalog;

public sealed record ZeroRule(string Code, string Description);

public sealed record CriterionSpec(string Name, int Weight, IReadOnlyDictionary<int, string> LevelDescriptors);

/// <summary>Grade de correção. Imutável depois de publicada: editar é publicar uma versão nova.</summary>
public sealed class RubricVersion : AggregateRoot
{
    public const int MinLevel = 1;
    public const int MaxLevel = 3;

    private readonly List<RubricCriterion> _criteria = [];

    private RubricVersion() { }

    public Guid Id { get; private set; }
    public Guid ExamId { get; private set; }
    public int Version { get; private set; }
    public DateTimeOffset PublishedAt { get; private set; }
    public int MaxScore { get; private set; }
    public IReadOnlyList<ZeroRule> ZeroRules { get; private set; } = [];
    public IReadOnlyList<RubricCriterion> Criteria => _criteria;

    public static RubricVersion Publish(
        Guid examId,
        int version,
        DateTimeOffset publishedAt,
        IReadOnlyList<ZeroRule> zeroRules,
        IReadOnlyList<CriterionSpec> criteria)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        if (criteria.Count == 0)
        {
            throw new ArgumentException("A grade precisa de ao menos um critério.", nameof(criteria));
        }

        var rubric = new RubricVersion
        {
            Id = Guid.CreateVersion7(),
            ExamId = examId,
            Version = version,
            PublishedAt = publishedAt,
            ZeroRules = [.. zeroRules],
        };

        for (var i = 0; i < criteria.Count; i++)
        {
            rubric._criteria.Add(RubricCriterion.Create(rubric.Id, i + 1, criteria[i]));
        }

        rubric.MaxScore = rubric._criteria.Sum(c => c.MaxPoints);
        return rubric;
    }
}

public sealed class RubricCriterion
{
    private RubricCriterion() { }

    public Guid Id { get; private set; }
    public Guid RubricVersionId { get; private set; }
    public int Position { get; private set; }
    public string Name { get; private set; } = null!;
    public int Weight { get; private set; }
    public IReadOnlyDictionary<int, string> LevelDescriptors { get; private set; } = new Dictionary<int, string>();

    public int MaxPoints => RubricVersion.MaxLevel * Weight;

    internal static RubricCriterion Create(Guid rubricVersionId, int position, CriterionSpec spec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.Name);
        ArgumentOutOfRangeException.ThrowIfLessThan(spec.Weight, 1);

        var levels = Enumerable.Range(RubricVersion.MinLevel, RubricVersion.MaxLevel);
        if (spec.LevelDescriptors.Count != RubricVersion.MaxLevel || !levels.All(spec.LevelDescriptors.ContainsKey))
        {
            throw new ArgumentException(
                $"O critério '{spec.Name}' precisa de um descritor para cada nível de 1 a 3.", nameof(spec));
        }

        return new RubricCriterion
        {
            Id = Guid.CreateVersion7(),
            RubricVersionId = rubricVersionId,
            Position = position,
            Name = spec.Name,
            Weight = spec.Weight,
            LevelDescriptors = new Dictionary<int, string>(spec.LevelDescriptors),
        };
    }
}
