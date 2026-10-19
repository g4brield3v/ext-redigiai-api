using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Jobs;

namespace RedigiAI.Application.Notifications;

/// <summary>
/// Os e-mails da plataforma (especificação, seção 10). Os da redação têm como referência o id da redação; os de
/// pagamento, o id da compra.
/// </summary>
public static class EmailTemplates
{
    public const string CorrectionReady = "correction_ready";
    public const string IllegibleWords = "illegible_words";
    public const string IllegibleReminder = "illegible_reminder";
    public const string ReviewExpired = "review_expired";
    public const string PhotoUnreadable = "photo_unreadable";
    public const string EssayFailed = "essay_failed";
    public const string PaymentApproved = "payment_approved";
    public const string SubscriptionPaymentFailed = "subscription_payment_failed";

    public static bool IsPayment(string template) => template is PaymentApproved or SubscriptionPaymentFailed;

    /// <summary>O aviso de uma redação que falhou ou foi cancelada, pelo código gravado nela.</summary>
    public static string? ForFailure(string? failureCode) => failureCode switch
    {
        null => null,
        "PHOTO_UNREADABLE" => PhotoUnreadable,
        "REVIEW_EXPIRED" => ReviewExpired,
        _ => EssayFailed,
    };
}

/// <param name="RefId">O id da redação ou, nos avisos de pagamento, da compra.</param>
public sealed record EmailJobPayload(string Template, Guid RefId);

public static class EmailJobs
{
    public const int Attempts = 6;

    /// <summary>Enfileira o aviso na transação do caso de uso: só sai se a mudança que o disparou for gravada.</summary>
    public static void Enqueue(IJobQueue jobs, string template, Guid refId) =>
        jobs.Enqueue(JobTypes.SendEmail, new EmailJobPayload(template, refId), Attempts);
}
