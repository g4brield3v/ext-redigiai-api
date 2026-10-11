namespace RedigiAI.Application.Abstractions;

/// <summary>
/// Executa um tipo de job da fila. Precisa ser idempotente: o mesmo job pode rodar mais de uma vez
/// (nova tentativa, worker que morreu no meio).
/// </summary>
public interface IJobHandler
{
    string Type { get; }

    /// <summary>
    /// Faz o trabalho. Um <c>AppError</c> não retentável encerra o job na hora; qualquer outra exceção
    /// leva a uma nova tentativa, até o limite do job.
    /// </summary>
    Task HandleAsync(string payload, CancellationToken ct);

    /// <summary>
    /// Falha definitiva (erro não retentável ou tentativas esgotadas): desfaz o que o job deixou pendente,
    /// como devolver o crédito. Também pode rodar mais de uma vez.
    /// </summary>
    Task OnDeadAsync(string payload, CancellationToken ct);
}

public interface IJobRunner
{
    /// <summary>Pega o próximo job pronto de um dos tipos e o executa até o fim.</summary>
    /// <returns><c>false</c> se não havia job para rodar.</returns>
    Task<bool> RunNextAsync(IReadOnlyCollection<string> types, CancellationToken ct);
}
