namespace RedigiAI.Application.Abstractions;

/// <summary>De onde veio a requisição, para o registro de acesso.</summary>
public sealed record RequestOrigin(string Ip, string? UserAgent, string? SessionId);

public interface IAuditLog
{
    /// <summary>Entra na transação do caso de uso. <paramref name="data"/> não leva dados pessoais.</summary>
    void Record(string actor, string action, string entity, Guid entityId, object? data = null);
}

public interface IAccessLog
{
    /// <summary>Entra na transação do caso de uso.</summary>
    void Record(Guid userId, RequestOrigin origin);

    /// <summary>Um registro por sessão de login; repetir a mesma sessão não grava de novo.</summary>
    Task RecordSessionOnceAsync(Guid userId, RequestOrigin origin, CancellationToken ct);
}
