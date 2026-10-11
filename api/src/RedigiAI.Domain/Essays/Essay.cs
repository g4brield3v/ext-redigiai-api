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
/// Redação enviada: responde a exatamente uma proposta (tema + tipo). Só o sistema muda o status;
/// depois de confirmado o envio, o aluno não troca a foto nem cancela.
/// </summary>
public sealed class Essay
{
    private Essay() { }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid BatchId { get; private set; }
    public Guid ExamId { get; private set; }
    public Guid ThemeId { get; private set; }
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
    public DateTimeOffset CreatedAt { get; private set; }

    /// <param name="rewriteOf">Redação que está sendo reescrita, se for o caso.</param>
    public static Essay Submit(
        Guid userId,
        Guid batchId,
        Exam exam,
        Theme theme,
        EssayType essayType,
        Asset photo,
        Essay? rewriteOf,
        DateTimeOffset now)
    {
        if (exam.Status != ExamStatus.Active || theme.Status != ThemeStatus.Active || theme.ExamId != exam.Id)
        {
            throw Errors.ThemeNotAvailable();
        }

        var accepted = theme.AcceptedTypes.FirstOrDefault(a => a.EssayTypeId == essayType.Id)
            ?? throw Errors.EssayTypeNotAccepted();

        if (photo.UserId != userId)
        {
            throw Errors.InvalidFile();
        }

        // A reescrita responde à mesma proposta, na conta de quem escreveu a primeira versão.
        if (rewriteOf is not null && (rewriteOf.UserId != userId || rewriteOf.ThemeId != theme.Id))
        {
            throw Errors.EssayNotFound();
        }

        return new Essay
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            BatchId = batchId,
            ExamId = exam.Id,
            ThemeId = theme.Id,
            EssayTypeId = essayType.Id,
            RubricVersionId = accepted.RubricVersionId,
            Title = EssayTitle.Compose(exam.Name, essayType.Name, theme.Title),
            Status = EssayStatus.Uploaded,
            PhotoAssetId = photo.Id,
            Version = (rewriteOf?.Version ?? 0) + 1,
            ParentEssayId = rewriteOf?.Id,
            CreatedAt = now,
        };
    }
}

public static class EssayTitle
{
    /// <summary>Formato único do produto: "Vestibular – Tipo – Tema", sem ano.</summary>
    public static string Compose(string examName, string essayTypeName, string themeTitle) =>
        $"{examName} – {essayTypeName} – {themeTitle}";
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
