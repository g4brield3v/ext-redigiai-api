using RedigiAI.Domain.Common;

namespace RedigiAI.Domain.Catalog;

public enum PromptStatus
{
    Draft,
    Published,
    Archived,
}

/// <summary>
/// Proposta de redação (docs/07-propostas-de-redacao.md): o tema, um único tipo, definido no cadastro, e o conteúdo
/// da página em blocos ordenados. Cada linha é uma versão: só o rascunho é editado; publicada, a proposta não muda
/// mais, e alterar é criar a versão seguinte (<see cref="NewVersion"/>). Cada redação guarda a versão que o aluno usou.
/// </summary>
public sealed class Prompt
{
    public const int MaxCreationKeyLength = 100;

    private readonly List<PromptBlock> _blocks = [];

    private Prompt() { }

    public Guid Id { get; private set; }
    public Guid ExamId { get; private set; }

    /// <summary>Endereço da proposta no site; não muda entre versões.</summary>
    public string Slug { get; private set; } = null!;

    /// <summary>1 na primeira versão; cada versão nova soma 1.</summary>
    public int Version { get; private set; }
    public PromptStatus Status { get; private set; }

    /// <summary>O tema: aparece na página, na lista e no título da redação. Sem ano.</summary>
    public string Title { get; private set; } = "";
    public string? Summary { get; private set; }
    public string? SourceLabel { get; private set; }

    /// <summary>Categoria de correção: escolhe a grade e o perfil de correção.</summary>
    public Guid? EssayTypeId { get; private set; }

    /// <summary>O tipo exatamente como a prova pede ("Dissertação em prosa"): vai para o aluno e para a IA.</summary>
    public string? GenreLabel { get; private set; }
    public int? SheetLinesMin { get; private set; }
    public int? SheetLinesMax { get; private set; }
    public bool TitleRequired { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }

    /// <summary>Administrador que criou a versão; nulo quando ela veio da carga inicial ou da conversão dos temas.</summary>
    public Guid? CreatedBy { get; private set; }
    public Guid? PublishedBy { get; private set; }

    /// <summary><c>Idempotency-Key</c> do pedido que criou o rascunho: repetir o pedido devolve o mesmo rascunho.</summary>
    public string? CreationKey { get; private set; }

    /// <summary>Os blocos, na ordem em que aparecem na página e vão para a IA.</summary>
    public IReadOnlyList<PromptBlock> Blocks => _blocks;

    /// <summary>Abordagens esperadas do tema: o que é estar no tema desta proposta. Só a IA vê.</summary>
    public PromptGradingNotes GradingNotes { get; private set; } = null!;

    /// <exception cref="AppError"><c>VALIDATION_FAILED</c> quando o conteúdo não pode nem ser guardado.</exception>
    public static Prompt Draft(PromptContent content, Guid? createdBy, DateTimeOffset now, string? creationKey = null)
    {
        var prompt = new Prompt
        {
            Id = Guid.CreateVersion7(),
            Version = 1,
            Status = PromptStatus.Draft,
            CreatedAt = now,
            CreatedBy = createdBy,
            CreationKey = creationKey,
        };
        prompt.GradingNotes = PromptGradingNotes.Empty(prompt.Id);
        prompt.Apply(content, now);
        return prompt;
    }

    /// <summary>Substitui o rascunho inteiro. Conteúdo incompleto é aceito; as pendências aparecem em <see cref="PublicationIssues"/>.</summary>
    /// <exception cref="AppError"><c>PROMPT_NOT_DRAFT</c> depois de publicada; <c>VALIDATION_FAILED</c> quando o conteúdo não pode ser guardado.</exception>
    public void Replace(PromptContent content, DateTimeOffset now)
    {
        EnsureDraft();
        Apply(content, now);
    }

    /// <summary>Muda só a ordem: cada bloco do rascunho recebe a posição informada.</summary>
    public void Reorder(IReadOnlyList<BlockPosition> order, DateTimeOffset now)
    {
        EnsureDraft();

        var issues = new List<ValidationIssue>();
        if (order.Count != _blocks.Count || order.Any(item => _blocks.All(block => block.Id != item.Id)))
        {
            issues.Add(new ValidationIssue("blocks", "Informe a posição de todos os blocos da proposta, e só deles."));
        }

        issues.AddRange(PromptRules.Positions([.. order.Select(item => item.Position)]));
        if (issues.Count > 0)
        {
            throw Errors.ValidationFailed(issues);
        }

        foreach (var item in order)
        {
            _blocks.Single(block => block.Id == item.Id).MoveTo(item.Position);
        }

        _blocks.Sort((a, b) => a.Position.CompareTo(b.Position));
        UpdatedAt = now;
    }

    /// <summary>O que falta para publicar (seção A4). Lista vazia = pode publicar.</summary>
    public IReadOnlyList<ValidationIssue> PublicationIssues(PublicationContext context) =>
        PromptRules.Publication(this, context);

    /// <exception cref="AppError"><c>PROMPT_NOT_DRAFT</c>; <c>VALIDATION_FAILED</c> com as pendências.</exception>
    public void Publish(PublicationContext context, Guid? publishedBy, DateTimeOffset now)
    {
        EnsureDraft();

        var issues = PublicationIssues(context);
        if (issues.Count > 0)
        {
            throw Errors.ValidationFailed(issues);
        }

        Status = PromptStatus.Published;
        PublishedAt = now;
        PublishedBy = publishedBy;
        UpdatedAt = now;
    }

    /// <summary>Sai da lista dos alunos. Nada é apagado: as redações feitas com esta versão continuam apontando para cá.</summary>
    public void Archive(DateTimeOffset now)
    {
        if (Status != PromptStatus.Published)
        {
            throw Errors.PromptInvalidState();
        }

        Status = PromptStatus.Archived;
        UpdatedAt = now;
    }

    /// <summary>Rascunho da versão seguinte, com o conteúdo desta. Só de uma versão que já foi publicada.</summary>
    /// <param name="version">A próxima versão livre desse endereço.</param>
    public Prompt NewVersion(int version, Guid? createdBy, DateTimeOffset now)
    {
        if (Status == PromptStatus.Draft)
        {
            throw Errors.PromptInvalidState();
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(version, Version);

        var draft = new Prompt
        {
            Id = Guid.CreateVersion7(),
            ExamId = ExamId,
            Slug = Slug,
            Version = version,
            Status = PromptStatus.Draft,
            Title = Title,
            Summary = Summary,
            SourceLabel = SourceLabel,
            EssayTypeId = EssayTypeId,
            GenreLabel = GenreLabel,
            SheetLinesMin = SheetLinesMin,
            SheetLinesMax = SheetLinesMax,
            TitleRequired = TitleRequired,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = createdBy,
        };
        draft._blocks.AddRange(_blocks.Select(block => PromptBlock.Create(draft.Id, block.Position, block.Kind, block.Data)));
        draft.GradingNotes = GradingNotes.CopyTo(draft.Id);
        return draft;
    }

    private void EnsureDraft()
    {
        if (Status != PromptStatus.Draft)
        {
            throw Errors.PromptNotDraft();
        }
    }

    private void Apply(PromptContent content, DateTimeOffset now)
    {
        var issues = PromptRules.Storage(content);
        if (issues.Count > 0)
        {
            throw Errors.ValidationFailed(issues);
        }

        ExamId = content.ExamId;
        Slug = content.Slug.Trim();
        Title = PromptRules.Clean(content.Title) ?? "";
        Summary = PromptRules.Clean(content.Summary);
        SourceLabel = PromptRules.Clean(content.SourceLabel);
        EssayTypeId = content.EssayTypeId;
        GenreLabel = PromptRules.Clean(content.GenreLabel);
        SheetLinesMin = content.MinLines;
        SheetLinesMax = content.MaxLines;
        TitleRequired = content.TitleRequired;

        // Bloco que já existe continua sendo o mesmo (mesmo id); os que não vieram saem.
        var kept = new HashSet<Guid>();
        foreach (var incoming in content.Blocks)
        {
            var data = PromptBlockData.For(incoming.Kind, incoming.Data);
            var existing = incoming.Id is { } id && kept.Add(id) ? _blocks.FirstOrDefault(block => block.Id == id) : null;
            if (existing is null)
            {
                var created = PromptBlock.Create(Id, incoming.Position, incoming.Kind, data);
                kept.Add(created.Id);
                _blocks.Add(created);
            }
            else
            {
                existing.Change(incoming.Position, incoming.Kind, data);
            }
        }

        _blocks.RemoveAll(block => !kept.Contains(block.Id));
        _blocks.Sort((a, b) => a.Position.CompareTo(b.Position));
        GradingNotes.Change(content.Notes);
        UpdatedAt = now;
    }
}

/// <summary>Uma versão na lista da administração, sem o conteúdo.</summary>
/// <param name="TextCount">Textos da coletânea.</param>
public sealed record PromptListItem(
    Guid Id,
    Guid ExamId,
    string Slug,
    int Version,
    PromptStatus Status,
    string Title,
    string? Summary,
    Guid? EssayTypeId,
    string? GenreLabel,
    int TextCount,
    DateTimeOffset UpdatedAt);

/// <summary>Nova posição de um bloco, para a reordenação.</summary>
public sealed record BlockPosition(Guid Id, int Position);

/// <summary>O que a publicação precisa saber e a proposta não tem como conferir sozinha.</summary>
/// <param name="EssayTypeHasActiveProfile">A categoria de correção existe no vestibular e tem perfil de correção ativo.</param>
/// <param name="ExamSheetLines">Linhas da folha do vestibular (30 na Fuvest).</param>
/// <param name="PromptImageAssetIds">Imagens de proposta que existem, entre as usadas nos blocos.</param>
public sealed record PublicationContext(bool EssayTypeHasActiveProfile, int ExamSheetLines, IReadOnlySet<Guid> PromptImageAssetIds);

/// <summary>Conteúdo de um rascunho, como veio do cadastro ou da importação.</summary>
public sealed record PromptContent(
    Guid ExamId,
    string Slug,
    string? Title,
    string? Summary,
    string? SourceLabel,
    Guid? EssayTypeId,
    string? GenreLabel,
    int? MinLines,
    int? MaxLines,
    bool TitleRequired,
    IReadOnlyList<PromptBlockContent> Blocks,
    PromptNotesContent Notes);

/// <param name="Id">O bloco que já existe no rascunho; ausente ou desconhecido, entra um bloco novo.</param>
public sealed record PromptBlockContent(Guid? Id, int Position, PromptBlockKind Kind, PromptBlockData Data);

public sealed record PromptNotesContent(
    string? ExpectedFocus, string? TangentSignals, string? OffTopicSignals, IReadOnlyList<string>? KeyData, string? Extra)
{
    public static PromptNotesContent Empty { get; } = new(null, null, null, null, null);
}

/// <summary>
/// Abordagens esperadas do tema: o recorte esperado, o que conta como tangenciar, o que é fuga e as ideias de cada
/// texto da coletânea. Uma por proposta. Não é o perfil de correção, que diz como corrigir qualquer redação.
/// </summary>
public sealed class PromptGradingNotes
{
    private PromptGradingNotes() { }

    public Guid Id { get; private set; }
    public Guid PromptId { get; private set; }
    public string ExpectedFocus { get; private set; } = "";
    public string TangentSignals { get; private set; } = "";
    public string OffTopicSignals { get; private set; } = "";
    public IReadOnlyList<string> KeyData { get; private set; } = [];
    public string Extra { get; private set; } = "";

    internal static PromptGradingNotes Empty(Guid promptId) => new() { Id = Guid.CreateVersion7(), PromptId = promptId };

    internal PromptGradingNotes CopyTo(Guid promptId) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            PromptId = promptId,
            ExpectedFocus = ExpectedFocus,
            TangentSignals = TangentSignals,
            OffTopicSignals = OffTopicSignals,
            KeyData = KeyData,
            Extra = Extra,
        };

    internal void Change(PromptNotesContent content)
    {
        ExpectedFocus = PromptRules.Clean(content.ExpectedFocus) ?? "";
        TangentSignals = PromptRules.Clean(content.TangentSignals) ?? "";
        OffTopicSignals = PromptRules.Clean(content.OffTopicSignals) ?? "";
        KeyData = [.. (content.KeyData ?? []).Select(PromptRules.Clean).OfType<string>()];
        Extra = PromptRules.Clean(content.Extra) ?? "";
    }
}
