namespace RedigiAI.Domain.Essays;

public interface IEssayRepository
{
    void Add(Essay essay);

    /// <summary>A redação do aluno; <c>null</c> se não existe ou é de outra conta.</summary>
    Task<Essay?> FindAsync(Guid essayId, Guid userId, CancellationToken ct);

    /// <summary>Redações do aluno, da mais recente para a mais antiga.</summary>
    /// <param name="before">Continua a lista depois desta redação (cursor da página anterior).</param>
    Task<IReadOnlyList<Essay>> ListByUserAsync(
        Guid userId, EssayStatus? status, Guid? before, int limit, CancellationToken ct);

    /// <summary>Entre as redações informadas, as que já foram reescritas.</summary>
    Task<IReadOnlySet<Guid>> RewrittenAsync(IReadOnlyCollection<Guid> essayIds, CancellationToken ct);
}

public interface IEssayBatchRepository
{
    void Add(EssayBatch batch);

    /// <summary>Ids das redações do envio feito com essa chave, na ordem em que foram enviadas; <c>null</c> se não houve.</summary>
    Task<IReadOnlyList<Guid>?> FindEssayIdsAsync(Guid userId, string idempotencyKey, CancellationToken ct);
}

public interface IAssetRepository
{
    void Add(Asset asset);

    /// <summary>O arquivo do aluno; <c>null</c> se não existe ou é de outra conta.</summary>
    Task<Asset?> FindAsync(Guid assetId, Guid userId, CancellationToken ct);
}
