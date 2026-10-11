namespace RedigiAI.Domain.Catalog;

public enum ExamStatus
{
    Active,
    ComingSoon,
}

public sealed class Exam
{
    private Exam() { }

    public Guid Id { get; private set; }
    public string Slug { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public ExamStatus Status { get; private set; }
    public int SheetLines { get; private set; }
    public int Position { get; private set; }

    public static Exam Create(string slug, string name, ExamStatus status, int sheetLines, int position)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(sheetLines, 1);

        return new Exam
        {
            Id = Guid.CreateVersion7(),
            Slug = slug,
            Name = name,
            Status = status,
            SheetLines = sheetLines,
            Position = position,
        };
    }
}
