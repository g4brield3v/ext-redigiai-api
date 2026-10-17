using System.Text.Json;
using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Accounts;
using RedigiAI.Domain.Catalog;
using RedigiAI.Domain.Common;
using RedigiAI.Domain.Essays;
using RedigiAI.Domain.Jobs;
using RedigiAI.Domain.Notifications;

namespace RedigiAI.Application.Notifications;

/// <summary>
/// Envia um aviso da redação. No máximo um envio por evento (<c>email_log</c> único em template + redação), e
/// só se o aviso ainda faz sentido: a redação continua no estado do aviso, a conta existe e, nos avisos que podem
/// ser desligados, o aluno não os desligou.
/// </summary>
internal sealed class SendEmailHandler(
    IEssayRepository essays,
    IUserRepository users,
    ITranscriptionRepository transcriptions,
    IPromptRepository prompts,
    IEmailLogRepository emailLog,
    IEmailSender sender,
    EmailComposer composer,
    IAuditLog audit,
    IUnitOfWork unitOfWork,
    TimeProvider time) : IJobHandler
{
    /// <summary>Reserva de envio mais antiga que isso, sem envio, é de um processo que caiu: pode ser retomada.</summary>
    private static readonly TimeSpan ClaimTimeout = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Type => JobTypes.SendEmail;

    public async Task HandleAsync(string payload, CancellationToken ct)
    {
        var job = JsonSerializer.Deserialize<EmailJobPayload>(payload, Json)!;
        if (await essays.FindByIdAsync(job.RefId, ct) is not { } essay || !StillApplies(job.Template, essay))
        {
            return;
        }

        if (await users.FindActiveByIdAsync(essay.UserId, ct) is not { } user || !Wanted(job.Template, user.EmailPreferences))
        {
            return;
        }

        var message = composer.Compose(job.Template, await FactsAsync(essay, user, ct));
        var now = time.GetUtcNow();
        if (await emailLog.TryClaimAsync(EmailLogEntry.Claim(user.Id, job.Template, essay.Id, now), now - ClaimTimeout, ct)
            is not { } claimed)
        {
            // Já enviado, ou outro processo está enviando agora.
            return;
        }

        string? providerMessageId;
        try
        {
            providerMessageId = await sender.SendAsync(message, ct);
        }
        catch
        {
            await emailLog.ReleaseAsync(claimed, CancellationToken.None);
            throw;
        }

        await emailLog.MarkSentAsync(claimed, providerMessageId, time.GetUtcNow(), ct);
    }

    public async Task OnDeadAsync(string payload, CancellationToken ct)
    {
        var job = JsonSerializer.Deserialize<EmailJobPayload>(payload, Json)!;
        audit.Record("system", "email.failed", "essay", job.RefId, new { template = job.Template });
        await unitOfWork.SaveChangesAsync(ct);
    }

    /// <summary>O aviso só sai se a redação ainda está no estado que o disparou.</summary>
    private static bool StillApplies(string template, Essay essay) => template switch
    {
        EmailTemplates.CorrectionReady => essay.Status == EssayStatus.Completed,
        EmailTemplates.IllegibleWords or EmailTemplates.IllegibleReminder => essay.Status == EssayStatus.AwaitingReview,
        EmailTemplates.ReviewExpired => essay.Status == EssayStatus.Cancelled,
        EmailTemplates.PhotoUnreadable or EmailTemplates.EssayFailed => essay.Status == EssayStatus.Failed,
        _ => throw new AppError("EMAIL_TEMPLATE_UNKNOWN", 500, "Algo deu errado do nosso lado. Tente novamente em instantes."),
    };

    /// <summary>Só a correção pronta e o lembrete podem ser desligados (especificação, seção 10).</summary>
    private static bool Wanted(string template, EmailPreferences preferences) => template switch
    {
        EmailTemplates.CorrectionReady => preferences.CorrectionReady,
        EmailTemplates.IllegibleReminder => preferences.IllegibleReminder,
        _ => true,
    };

    private async Task<EssayEmailFacts> FactsAsync(Essay essay, User user, CancellationToken ct)
    {
        var illegible = essay.Status == EssayStatus.AwaitingReview
            ? (await transcriptions.FindByEssayAsync(essay.Id, ct))?.IllegibleCount ?? 0
            : 0;
        var slug = (await prompts.ReferencesAsync([essay.PromptId], ct))[essay.PromptId].Slug;

        return new EssayEmailFacts(
            user.Email,
            user.Name,
            essay.Id,
            essay.Title,
            illegible,
            essay.ReviewDeadlineAt,
            essay.FailureCode,
            essay.FailureDetails?.IllegibleCount,
            slug);
    }
}
