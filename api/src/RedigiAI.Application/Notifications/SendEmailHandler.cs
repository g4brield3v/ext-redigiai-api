using System.Text.Json;
using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Accounts;
using RedigiAI.Domain.Billing;
using RedigiAI.Domain.Catalog;
using RedigiAI.Domain.Common;
using RedigiAI.Domain.Essays;
using RedigiAI.Domain.Jobs;
using RedigiAI.Domain.Notifications;

namespace RedigiAI.Application.Notifications;

/// <summary>
/// Envia um aviso da redação, se o envio estiver ligado no banco (<c>feature_flags.email_sending</c>). No máximo um
/// envio por evento (<c>email_log</c> único em template + redação), e só se o aviso ainda faz sentido: a redação
/// continua no estado do aviso, a conta existe e, nos avisos que podem ser desligados, o aluno não os desligou.
/// </summary>
internal sealed class SendEmailHandler(
    IFeatureFlagRepository flags,
    IEssayRepository essays,
    IUserRepository users,
    ITranscriptionRepository transcriptions,
    IPromptRepository prompts,
    IPaymentRepository payments,
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
        // Envio desligado no banco: o aviso é descartado (não fica para depois), para que ligar o envio não dispare
        // de uma vez avisos que já perderam o sentido.
        if (!await flags.IsEnabledAsync(FeatureFlag.EmailSending, ct))
        {
            return;
        }

        var job = JsonSerializer.Deserialize<EmailJobPayload>(payload, Json)!;
        if (EmailTemplates.IsPayment(job.Template))
        {
            await HandlePaymentAsync(job, ct);
            return;
        }

        if (await essays.FindByIdAsync(job.RefId, ct) is not { } essay || !StillApplies(job.Template, essay))
        {
            return;
        }

        if (await users.FindActiveByIdAsync(essay.UserId, ct) is not { } user || !Wanted(job.Template, user.EmailPreferences))
        {
            return;
        }

        await SendOnceAsync(user.Id, job.Template, essay.Id, composer.Compose(job.Template, await FactsAsync(essay, user, ct)), ct);
    }

    /// <summary>O aviso de pagamento só sai se a compra continua aprovada e a conta existe.</summary>
    private async Task HandlePaymentAsync(EmailJobPayload job, CancellationToken ct)
    {
        if (await payments.FindAsync(job.RefId, ct) is not { Status: PaymentStatus.Approved, ApprovedAt: { } approvedAt } payment
            || await users.FindActiveByIdAsync(payment.UserId, ct) is not { } user)
        {
            return;
        }

        var facts = new PaymentEmailFacts(
            user.Email, user.Name, payment.Id, payment.Quantity, payment.AmountCents, approvedAt + Payment.RefundWindow);
        await SendOnceAsync(user.Id, job.Template, payment.Id, composer.ComposePayment(job.Template, facts), ct);
    }

    /// <summary>Reserva, envia e marca: no máximo um envio por template e referência (<c>email_log</c>).</summary>
    private async Task SendOnceAsync(Guid userId, string template, Guid refId, EmailMessage message, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var claim = await emailLog.TryClaimAsync(EmailLogEntry.Claim(userId, template, refId, now), now - ClaimTimeout, ct);
        if (claim.AlreadySent)
        {
            return;
        }

        // Outro processo reservou há pouco e pode ter caído no meio: o job tenta de novo mais tarde, quando a
        // reserva já puder ser retomada, em vez de dar o e-mail por enviado.
        var claimed = claim.Id ?? throw new AppError(
            "EMAIL_BUSY", 503, "Algo deu errado do nosso lado. Tente novamente em instantes.", retryable: true);

        string? providerMessageId;
        try
        {
            providerMessageId = await sender.SendAsync(message, ct);
        }
        catch (Exception failure) when (failure is AppError or HttpRequestException)
        {
            // Recusa ou indisponibilidade informada pelo provedor: nada saiu, a nova tentativa pode seguir. Tempo
            // esgotado e cancelamento ficam com a reserva (o e-mail pode ter saído); ela vence em 10 minutos.
            await emailLog.ReleaseAsync(claimed, CancellationToken.None);
            throw;
        }

        await emailLog.MarkSentAsync(claimed, providerMessageId, time.GetUtcNow(), ct);
    }

    public async Task OnDeadAsync(string payload, CancellationToken ct)
    {
        var job = JsonSerializer.Deserialize<EmailJobPayload>(payload, Json)!;
        audit.Record("system", "email.failed", EmailTemplates.IsPayment(job.Template) ? "payment" : "essay", job.RefId, new { template = job.Template });
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
