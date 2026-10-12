using RedigiAI.Domain.Catalog;
using RedigiAI.Domain.Common;

namespace RedigiAI.Domain.Essays;

public enum EssayStatus
{
    Uploaded,
    AwaitingReview,
    Queued,
    Processing,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// Redação enviada: responde a exatamente uma proposta, na versão publicada no envio. Só o sistema muda o status;
/// depois de confirmado o envio, o aluno não troca a foto nem cancela.
/// </summary>
public sealed class Essay
{
    private Essay() { }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid BatchId { get; private set; }
    public Guid ExamId { get; private set; }
    /// <summary>A versão exata da proposta que o aluno usou.</summary>
    public Guid PromptId { get; private set; }

    /// <summary>Copiado da proposta no envio: o aluno não escolhe tipo.</summary>
    public Guid EssayTypeId { get; private set; }
    public Guid RubricVersionId { get; private set; }

    /// <summary>"Vestibular – Tipo – Tema", fixado no envio.</summary>
    public string Title { get; private set; } = null!;
    public EssayStatus Status { get; private set; }
    public Guid PhotoAssetId { get; private set; }

    /// <summary>1 no primeiro envio; cada reescrita soma 1 à versão anterior.</summary>
    public int Version { get; private set; }
    public Guid? ParentEssayId { get; private set; }
    public DateTimeOffset? ReviewDeadlineAt { get; private set; }
    public string? FailureCode { get; private set; }

    /// <summary>Perfil de correção fixado quando a correção começa; uma nova tentativa usa o mesmo.</summary>
    public Guid? GradingProfileId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <param name="essayType">A categoria de correção da proposta.</param>
    /// <param name="rubricVersionId">A grade em uso para esse vestibular e categoria.</param>
    /// <param name="rewriteOf">Redação que está sendo reescrita, se for o caso.</param>
    public static Essay Submit(
        Guid userId,
        Guid batchId,
        Exam exam,
        Prompt prompt,
        EssayType essayType,
        Guid rubricVersionId,
        Asset photo,
        Essay? rewriteOf,
        DateTimeOffset now)
    {
        // A reescrita responde à mesma versão da proposta, na conta de quem escreveu a primeira, mesmo que a
        // proposta já tenha uma versão mais nova ou tenha saído da lista.
        if (rewriteOf is not null && (rewriteOf.UserId != userId || rewriteOf.PromptId != prompt.Id))
        {
            throw Errors.EssayNotFound();
        }

        var available = rewriteOf is null ? prompt.Status == PromptStatus.Published : prompt.Status != PromptStatus.Draft;
        if (exam.Status != ExamStatus.Active || !available || prompt.ExamId != exam.Id || prompt.EssayTypeId != essayType.Id)
        {
            throw Errors.PromptNotAvailable();
        }

        if (photo.UserId != userId)
        {
            throw Errors.InvalidFile();
        }

        return new Essay
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            BatchId = batchId,
            ExamId = exam.Id,
            PromptId = prompt.Id,
            EssayTypeId = essayType.Id,
            RubricVersionId = rubricVersionId,
            Title = EssayTitle.Compose(exam.Name, essayType.Name, prompt.Title),
            Status = EssayStatus.Uploaded,
            PhotoAssetId = photo.Id,
            Version = (rewriteOf?.Version ?? 0) + 1,
            ParentEssayId = rewriteOf?.Id,
            CreatedAt = now,
        };
    }
}

/// <summary>
/// Uma passagem da máquina de estados (especificação, seção 4). Só existem as passagens criadas aqui;
/// o repositório grava cada uma com <c>UPDATE … WHERE status = From</c>.
/// </summary>
public sealed record EssayTransition
{
    public static readonly TimeSpan ReviewWindow = TimeSpan.FromHours(24);

    private EssayTransition(EssayStatus from, EssayStatus to)
    {
        From = from;
        To = to;
    }

    public EssayStatus From { get; }

    public EssayStatus To { get; }

    public DateTimeOffset? ReviewDeadlineAt { get; private init; }

    public string? FailureCode { get; private init; }

    /// <summary>Perfil de correção que passa a valer para a redação; nulo mantém o que já está gravado.</summary>
    public Guid? GradingProfileId { get; private init; }

    /// <summary>Transcrição sem palavras ilegíveis: vai para a fila de correção.</summary>
    public static EssayTransition TranscribedClean() => new(EssayStatus.Uploaded, EssayStatus.Queued);

    /// <summary>De 1 a 10 palavras ilegíveis: o aluno tem 24 h para digitá-las.</summary>
    public static EssayTransition NeedsReview(DateTimeOffset now) =>
        new(EssayStatus.Uploaded, EssayStatus.AwaitingReview) { ReviewDeadlineAt = now + ReviewWindow };

    /// <summary>Mais de 10 palavras ilegíveis ou foto que não dá para ler.</summary>
    public static EssayTransition PhotoUnreadable() =>
        new(EssayStatus.Uploaded, EssayStatus.Failed) { FailureCode = "PHOTO_UNREADABLE" };

    public static EssayTransition TranscriptionFailed() =>
        new(EssayStatus.Uploaded, EssayStatus.Failed) { FailureCode = "TRANSCRIPTION_FAILED" };

    /// <summary>O aluno preencheu todas as palavras.</summary>
    public static EssayTransition ReviewCompleted() => new(EssayStatus.AwaitingReview, EssayStatus.Queued);

    /// <summary>24 h sem preencher: cancelada pelo sistema.</summary>
    public static EssayTransition ReviewExpired() =>
        new(EssayStatus.AwaitingReview, EssayStatus.Cancelled) { FailureCode = "REVIEW_EXPIRED" };

    /// <summary>O worker pegou o job de correção: o perfil ativo fica fixado na redação.</summary>
    public static EssayTransition GradingStarted(Guid gradingProfileId) =>
        new(EssayStatus.Queued, EssayStatus.Processing) { GradingProfileId = gradingProfileId };

    /// <summary>Correção validada e gravada.</summary>
    public static EssayTransition Graded() => new(EssayStatus.Processing, EssayStatus.Completed);

    /// <summary>Falha definitiva da correção.</summary>
    /// <param name="started">A correção chegou a começar (<c>processing</c>) ou falhou ainda na fila.</param>
    public static EssayTransition GradingFailed(bool started) =>
        new(started ? EssayStatus.Processing : EssayStatus.Queued, EssayStatus.Failed) { FailureCode = "GRADING_FAILED" };
}

public static class EssayTitle
{
    /// <summary>Formato único do produto: "Vestibular – Tipo – Tema", sem ano.</summary>
    public static string Compose(string examName, string essayTypeName, string promptTitle) =>
        $"{examName} – {essayTypeName} – {promptTitle}";
}

/// <summary>Uma confirmação de envio (uma ou várias redações). A chave de idempotência é única por aluno.</summary>
public sealed class EssayBatch
{
    public const int MaxEssays = 10;
    public const int MaxKeyLength = 100;

    private EssayBatch() { }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string IdempotencyKey { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }

    public static EssayBatch Create(Guid userId, string idempotencyKey, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            IdempotencyKey = idempotencyKey,
            CreatedAt = now,
        };
}
