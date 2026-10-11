namespace RedigiAI.Domain.Catalog;

public enum ThemeStatus
{
    Draft,
    Active,
    Archived,
}

public enum ThemeTextKind
{
    Text,
    Image,
}

/// <summary>Tema da proposta, com a coletânea e os tipos de redação aceitos. Sem ano no título.</summary>
public sealed class Theme
{
    private readonly List<ThemeText> _texts = [];
    private readonly List<ThemeEssayType> _acceptedTypes = [];

    private Theme() { }

    public Guid Id { get; private set; }
    public Guid ExamId { get; private set; }
    public string Title { get; private set; } = null!;
    public string Statement { get; private set; } = null!;
    public ThemeStatus Status { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }
    public IReadOnlyList<ThemeText> Texts => _texts;
    public IReadOnlyList<ThemeEssayType> AcceptedTypes => _acceptedTypes;

    public static Theme Draft(Guid examId, string title, string statement)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(statement);

        return new Theme
        {
            Id = Guid.CreateVersion7(),
            ExamId = examId,
            Title = title,
            Statement = statement,
            Status = ThemeStatus.Draft,
        };
    }

    public void AddText(ThemeTextKind kind, string title, string source, string? body)
    {
        _texts.Add(ThemeText.Create(Id, _texts.Count + 1, kind, title, source, body));
    }

    /// <param name="instruction">O que a proposta pede para esse tipo de texto.</param>
    public void AcceptType(EssayType essayType, RubricVersion rubric, string instruction)
    {
        if (essayType.ExamId != ExamId || rubric.ExamId != ExamId)
        {
            throw new InvalidOperationException("Tipo de redação e grade precisam ser do mesmo vestibular do tema.");
        }

        if (_acceptedTypes.Any(t => t.EssayTypeId == essayType.Id))
        {
            return;
        }

        _acceptedTypes.Add(new ThemeEssayType(Id, essayType.Id, rubric.Id, instruction));
    }

    public void Publish(DateTimeOffset now)
    {
        if (_acceptedTypes.Count == 0)
        {
            throw new InvalidOperationException("Um tema só é publicado com ao menos um tipo de redação aceito.");
        }

        Status = ThemeStatus.Active;
        PublishedAt ??= now;
    }
}

public sealed class ThemeText
{
    private ThemeText() { }

    public Guid Id { get; private set; }
    public Guid ThemeId { get; private set; }
    public int Position { get; private set; }
    public ThemeTextKind Kind { get; private set; }
    public string Title { get; private set; } = null!;
    public string Source { get; private set; } = null!;
    public string? Body { get; private set; }
    public Guid? ImageAssetId { get; private set; }

    internal static ThemeText Create(
        Guid themeId, int position, ThemeTextKind kind, string title, string source, string? body) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            ThemeId = themeId,
            Position = position,
            Kind = kind,
            Title = title,
            Source = source,
            Body = body,
        };
}

public sealed class ThemeEssayType
{
    private ThemeEssayType() { }

    internal ThemeEssayType(Guid themeId, Guid essayTypeId, Guid rubricVersionId, string instruction)
    {
        Instruction = instruction;
        ThemeId = themeId;
        EssayTypeId = essayTypeId;
        RubricVersionId = rubricVersionId;
    }

    public Guid ThemeId { get; private set; }
    public Guid EssayTypeId { get; private set; }
    public Guid RubricVersionId { get; private set; }
    public string Instruction { get; private set; } = "";
}
