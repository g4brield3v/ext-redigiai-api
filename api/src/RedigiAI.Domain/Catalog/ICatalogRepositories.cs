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

    /// <summary>Id da versão publicada mais recente de cada vestibular que tem grade.</summary>
    Task<IReadOnlyDictionary<Guid, Guid>> LatestVersionIdsAsync(CancellationToken ct);

    void Add(RubricVersion rubric);
}

public interface IThemeRepository
{
    /// <summary>Temas ativos do vestibular, do mais recente para o mais antigo, com textos e tipos aceitos.</summary>
    /// <param name="titleSearch">Trecho do título, comparado literalmente e sem diferenciar maiúsculas.</param>
    Task<IReadOnlyList<Theme>> ListActiveAsync(Guid examId, string? titleSearch, CancellationToken ct);

    /// <summary>O tema ativo, com textos e tipos aceitos.</summary>
    Task<Theme?> FindActiveAsync(Guid themeId, CancellationToken ct);

    void Add(Theme theme);
}
