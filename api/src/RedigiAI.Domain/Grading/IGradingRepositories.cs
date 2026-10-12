namespace RedigiAI.Domain.Grading;

public interface IGradingProfileRepository
{
    void Add(GradingProfile profile);

    /// <summary>O perfil que corrige os alunos desse vestibular e tipo agora; <c>null</c> se não há perfil ativo.</summary>
    Task<GradingProfile?> FindActiveAsync(Guid examId, Guid essayTypeId, CancellationToken ct);
}

public interface IPromptTemplateRepository
{
    void Add(PromptTemplate template);

    Task<PromptTemplate?> FindAsync(Guid id, CancellationToken ct);

    /// <summary>A versão mais recente das instruções desse tipo; <c>null</c> se ainda não existe nenhuma.</summary>
    Task<PromptTemplate?> FindLatestAsync(PromptKind kind, CancellationToken ct);
}

public interface IGuidelinesRepository
{
    void Add(GuidelinesVersion guidelines);

    Task<GuidelinesVersion?> FindAsync(Guid id, CancellationToken ct);

    Task<GuidelinesVersion?> FindLatestAsync(Guid examId, CancellationToken ct);
}

public interface IWritingRulesRepository
{
    void Add(WritingRulesVersion rules);

    Task<WritingRulesVersion?> FindAsync(Guid id, CancellationToken ct);

    Task<WritingRulesVersion?> FindLatestAsync(CancellationToken ct);
}

public interface IThemeGradingNotesRepository
{
    void Add(ThemeGradingNotes notes);

    /// <summary>A versão mais recente das notas do tema; <c>null</c> se o tema não tem notas.</summary>
    Task<ThemeGradingNotes?> FindLatestAsync(Guid themeId, CancellationToken ct);
}
