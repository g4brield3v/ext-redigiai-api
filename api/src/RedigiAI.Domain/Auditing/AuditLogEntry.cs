namespace RedigiAI.Domain.Auditing;

public sealed class AuditLogEntry
{
    private AuditLogEntry() { }

    public Guid Id { get; private set; }
    public string Actor { get; private set; } = null!;
    public string Action { get; private set; } = null!;
    public string Entity { get; private set; } = null!;
    public Guid EntityId { get; private set; }
    public string? Data { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <param name="data">JSON sem dados pessoais nem conteúdo de redação.</param>
    public static AuditLogEntry Create(
        string actor, string action, string entity, Guid entityId, string? data, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Actor = actor,
            Action = action,
            Entity = entity,
            EntityId = entityId,
            Data = data,
            CreatedAt = now,
        };
}
