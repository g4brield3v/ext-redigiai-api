namespace RedigiAI.Domain.Auditing;

/// <summary>Registro de acesso (Marco Civil, art. 15), guardado por 6 meses mesmo após a exclusão da conta.</summary>
public sealed class AccessLogEntry
{
    public const int MaxUserAgentLength = 512;

    private AccessLogEntry() { }

    public Guid Id { get; private set; }
    public Guid? UserId { get; private set; }
    public string Ip { get; private set; } = null!;
    public string? UserAgent { get; private set; }
    public string? SessionId { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }

    public static AccessLogEntry Create(
        Guid userId, string ip, string? userAgent, string? sessionId, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Ip = ip,
            UserAgent = userAgent is { Length: > MaxUserAgentLength } ? userAgent[..MaxUserAgentLength] : userAgent,
            SessionId = sessionId,
            OccurredAt = now,
        };
}
