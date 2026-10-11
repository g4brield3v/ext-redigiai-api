namespace RedigiAI.Application.Abstractions;

public interface IUnitOfWork
{
    /// <summary>Grava tudo o que o caso de uso alterou numa única transação.</summary>
    /// <exception cref="UniqueConflictException">Outra transação gravou a mesma chave única antes.</exception>
    Task SaveChangesAsync(CancellationToken ct);
}

/// <summary>Violação de chave única: outro processo já fez a mesma operação. Nada foi gravado.</summary>
public sealed class UniqueConflictException(Exception inner) : Exception("Unique constraint violated.", inner);
