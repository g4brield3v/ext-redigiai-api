using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Jobs;

namespace RedigiAI.Application.Notifications;

/// <summary>Os e-mails da redação (especificação, seção 10). Todos têm como referência o id da redação.</summary>
public static class EmailTemplates
{
    public const string CorrectionReady = "correction_ready";
    public const string IllegibleWords = "illegible_words";
    public const string IllegibleReminder = "illegible_reminder";
    public const string ReviewExpired = "review_expired";
    public const string PhotoUnreadable = "photo_unreadable";
    public const string EssayFailed = "essay_failed";

    /// <summary>O aviso de uma redação que falhou ou foi cancelada, pelo código gravado nela.</summary>
    public static string? ForFailure(string? failureCode) => failureCode switch
    {
        null => null,
        "PHOTO_UNREADABLE" => PhotoUnreadable,
        "REVIEW_EXPIRED" => ReviewExpired,
        _ => EssayFailed,
    };
}

/// <param name="RefId">O id da redação.</param>
public sealed record EmailJobPayload(string Template, Guid RefId);

public static class EmailJobs
{
    public const int Attempts = 6;

    /// <summary>Enfileira o aviso na transação do caso de uso: só sai se a mudança que o disparou for gravada.</summary>
    public static void Enqueue(IJobQueue jobs, string template, Guid essayId) =>
        jobs.Enqueue(JobTypes.SendEmail, new EmailJobPayload(template, essayId), Attempts);
}
