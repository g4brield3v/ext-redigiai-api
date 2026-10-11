namespace RedigiAI.Domain.Catalog;

public sealed class EssayType
{
    private EssayType() { }

    public Guid Id { get; private set; }
    public Guid ExamId { get; private set; }
    public string Slug { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public string Description { get; private set; } = null!;

    public static EssayType Create(Guid examId, string slug, string name, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new EssayType
        {
            Id = Guid.CreateVersion7(),
            ExamId = examId,
            Slug = slug,
            Name = name,
            Description = description,
        };
    }
}
