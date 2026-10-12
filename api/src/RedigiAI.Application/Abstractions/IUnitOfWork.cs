namespace RedigiAI.Application.Abstractions;

public interface IUnitOfWork
{
    /// <summary>Grava tudo o que o caso de uso alterou numa única transação.</summary>
    /// <exception cref="UniqueConflictException">Outra transação gravou a mesma chave única antes.</exception>
    Task SaveChangesAsync(CancellationToken ct);

    /// <summary>
    /// Roda o caso de uso numa transação explícita e grava no fim. Necessário quando há escritas diretas
    /// (como o débito condicional de crédito) que precisam valer ou cair junto com o resto.
    /// Qualquer exceção desfaz tudo.
    /// </summary>
    /// <exception cref="UniqueConflictException">Outra transação gravou a mesma chave única antes.</exception>
    Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken ct);
}

/// <summary>Violação de chave única: outro processo já fez a mesma operação. Nada foi gravado.</summary>
public sealed class UniqueConflictException(Exception inner) : Exception("Unique constraint violated.", inner);
