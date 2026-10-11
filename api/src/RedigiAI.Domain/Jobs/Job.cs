namespace RedigiAI.Domain.Jobs;

public enum JobStatus
{
    Ready,
    Running,
    Done,
    Dead,
}

public static class JobTypes
{
    public const string Transcribe = "transcribe";
}

/// <summary>Trabalho da fila. É gravado na mesma transação da mudança que o disparou.</summary>
public sealed class Job
{
    /// <summary>Se o worker morrer, o job volta a ficar disponível depois disso.</summary>
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(8), TimeSpan.FromMinutes(30),
    ];

    private Job() { }

    public Guid Id { get; private set; }
    public string Type { get; private set; } = null!;

    /// <summary>JSON com os ids de que o handler precisa; nunca conteúdo de redação.</summary>
    public string Payload { get; private set; } = null!;
    public JobStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public int MaxAttempts { get; private set; }
    public DateTimeOffset RunAt { get; private set; }
    public DateTimeOffset? LockedUntil { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static Job Create(string type, string payload, int maxAttempts, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        return new Job
        {
            Id = Guid.CreateVersion7(),
            Type = type,
            Payload = payload,
            Status = JobStatus.Ready,
            MaxAttempts = maxAttempts,
            RunAt = now,
            CreatedAt = now,
        };
    }

    /// <summary>Espera antes da próxima tentativa: 30 s, 2 min, 8 min e 30 min.</summary>
    /// <param name="attempts">Tentativas já feitas, contando a que acabou de falhar.</param>
    public static TimeSpan BackoffAfter(int attempts) =>
        Backoff[Math.Clamp(attempts - 1, 0, Backoff.Length - 1)];
}
