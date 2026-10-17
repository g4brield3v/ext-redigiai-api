namespace RedigiAI.Domain.Essays;

public interface IEssayRepository
{
    void Add(Essay essay);

    /// <summary>A redação do aluno; <c>null</c> se não existe ou é de outra conta.</summary>
    Task<Essay?> FindAsync(Guid essayId, Guid userId, CancellationToken ct);

    /// <summary>Para os jobs, que agem pela redação e não por um aluno logado.</summary>
    Task<Essay?> FindByIdAsync(Guid essayId, CancellationToken ct);

    /// <summary>Redações do aluno, da mais recente para a mais antiga.</summary>
    /// <param name="before">Continua a lista depois desta redação (cursor da página anterior).</param>
    Task<IReadOnlyList<Essay>> ListByUserAsync(
        Guid userId, EssayStatus? status, Guid? before, int limit, CancellationToken ct);

    /// <summary>Entre as redações informadas, as que já foram reescritas.</summary>
    Task<IReadOnlySet<Guid>> RewrittenAsync(IReadOnlyCollection<Guid> essayIds, CancellationToken ct);

    /// <summary>
    /// Muda o status com <c>UPDATE … WHERE status = esperado</c> (regra 10), na hora, dentro da transação
    /// aberta pelo caso de uso.
    /// </summary>
    /// <returns><c>false</c> se outro processo já avançou a redação; quem chamou sai sem efeito.</returns>
    Task<bool> TryMoveAsync(Guid essayId, EssayTransition transition, CancellationToken ct);

    /// <summary>Redações à espera da revisão do aluno com o prazo vencido.</summary>
    Task<IReadOnlyList<Guid>> ListExpiredReviewsAsync(DateTimeOffset now, int limit, CancellationToken ct);

    /// <summary>
    /// Registros da trilha de auditoria ligados à redação, do mais antigo para o mais novo. Quem chama já
    /// conferiu que a redação é do aluno.
    /// </summary>
    Task<IReadOnlyList<EssayHistoryEntry>> ListHistoryAsync(Guid essayId, CancellationToken ct);
}

/// <param name="Data">JSON gravado com o registro; nunca vai direto para o aluno.</param>
public sealed record EssayHistoryEntry(string Action, string? Data, DateTimeOffset At);

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

    Task<Asset?> FindByIdAsync(Guid assetId, CancellationToken ct);

    /// <summary>Entre os ids pedidos, os que são imagens de proposta, com o identificador de cada uma no provedor.</summary>
    Task<IReadOnlyDictionary<Guid, string>> PromptImagePublicIdsAsync(IReadOnlyCollection<Guid> assetIds, CancellationToken ct);
}

public interface ITranscriptionRepository
{
    void Add(Transcription transcription);

    /// <summary>A transcrição da redação, com as palavras ilegíveis, pronta para receber o que o aluno digitou.</summary>
    Task<Transcription?> FindByEssayAsync(Guid essayId, CancellationToken ct);

    /// <summary>Quantas palavras ilegíveis cada redação tem.</summary>
    Task<IReadOnlyDictionary<Guid, int>> IllegibleCountsAsync(IReadOnlyCollection<Guid> essayIds, CancellationToken ct);
}

public interface IAiUsageRepository
{
    void Add(AiUsage usage);

    /// <summary>Custo já registrado das chamadas dessa redação, da mais antiga para a mais recente.</summary>
    Task<IReadOnlyList<long>> ListCostsAsync(Guid essayId, CancellationToken ct);
}
