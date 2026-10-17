namespace RedigiAI.Domain.Catalog;

public interface IExamRepository
{
    /// <summary>Todos os vestibulares, na ordem de exibição.</summary>
    Task<IReadOnlyList<Exam>> ListAsync(CancellationToken ct);

    Task<Exam?> FindBySlugAsync(string slug, CancellationToken ct);

    Task<Exam?> FindByIdAsync(Guid id, CancellationToken ct);

    void Add(Exam exam);
}

public interface IEssayTypeRepository
{
    Task<IReadOnlyList<EssayType>> ListByExamAsync(Guid examId, CancellationToken ct);

    void Add(EssayType essayType);
}

public interface IRubricRepository
{
    /// <summary>A versão da grade, com os critérios.</summary>
    Task<RubricVersion?> FindAsync(Guid rubricVersionId, CancellationToken ct);

    /// <summary>A versão publicada mais recente do vestibular, com os critérios.</summary>
    Task<RubricVersion?> FindLatestAsync(Guid examId, CancellationToken ct);

    /// <summary>Id da versão publicada mais recente de cada vestibular que tem grade.</summary>
    Task<IReadOnlyDictionary<Guid, Guid>> LatestVersionIdsAsync(CancellationToken ct);

    void Add(RubricVersion rubric);
}

public interface IPromptRepository
{
    void Add(Prompt prompt);

    /// <summary>A versão, com os blocos e as abordagens esperadas, pronta para ser alterada.</summary>
    Task<Prompt?> FindForUpdateAsync(Guid promptId, CancellationToken ct);

    /// <summary>A versão em qualquer situação, só para leitura: a correção usa a versão com que a redação foi enviada.</summary>
    Task<Prompt?> FindAsync(Guid promptId, CancellationToken ct);

    /// <summary>A versão publicada desse endereço; <c>null</c> se não há nenhuma.</summary>
    Task<Prompt?> FindPublishedAsync(string slug, CancellationToken ct);

    /// <summary>Uma versão que já foi publicada (publicada ou arquivada) desse endereço: a que uma redação antiga usou.</summary>
    Task<Prompt?> FindReleasedAsync(string slug, int version, CancellationToken ct);

    /// <summary>Propostas publicadas do vestibular, da mais recente para a mais antiga.</summary>
    /// <param name="titleSearch">Trecho do título, comparado literalmente e sem diferenciar maiúsculas.</param>
    Task<IReadOnlyList<PromptListItem>> ListPublishedAsync(Guid examId, string? titleSearch, CancellationToken ct);

    /// <summary>Endereço e versão de cada proposta pedida, para os links das redações.</summary>
    Task<IReadOnlyDictionary<Guid, PromptReference>> ReferencesAsync(IReadOnlyCollection<Guid> promptIds, CancellationToken ct);

    Task<bool> AnyAsync(Guid examId, CancellationToken ct);

    /// <summary>
    /// Segura a linha da versão até o fim da transação: dois salvamentos, ou um salvamento e uma publicação, da mesma
    /// versão passam um de cada vez.
    /// </summary>
    Task LockAsync(Guid promptId, CancellationToken ct);

    /// <summary>O rascunho criado por esse administrador com essa chave de idempotência.</summary>
    Task<Prompt?> FindByCreationKeyAsync(Guid adminId, string creationKey, CancellationToken ct);

    /// <summary>Todas as versões, da alterada mais recentemente para a mais antiga.</summary>
    /// <param name="titleSearch">Trecho do título, comparado literalmente e sem diferenciar maiúsculas.</param>
    Task<IReadOnlyList<PromptListItem>> ListAsync(Guid? examId, string? titleSearch, CancellationToken ct);

    /// <summary>A maior versão que existe nesse endereço; 0 se o endereço está livre.</summary>
    Task<int> MaxVersionAsync(string slug, CancellationToken ct);

    Task<bool> HasDraftAsync(string slug, CancellationToken ct);

    /// <summary>Arquiva a versão publicada desse endereço, se houver. Devolve quantas linhas mudaram.</summary>
    Task<int> ArchivePublishedAsync(string slug, DateTimeOffset now, CancellationToken ct);

    /// <summary><c>draft → published</c>. Devolve se a linha mudou (regra 10).</summary>
    Task<bool> TryPublishAsync(Guid promptId, Guid? publishedBy, DateTimeOffset now, CancellationToken ct);

    /// <summary><c>published → archived</c>. Devolve se a linha mudou (regra 10).</summary>
    Task<bool> TryArchiveAsync(Guid promptId, DateTimeOffset now, CancellationToken ct);

    /// <summary>Apaga a versão só se ela ainda é rascunho. Devolve se apagou.</summary>
    Task<bool> TryDeleteDraftAsync(Guid promptId, CancellationToken ct);
}
