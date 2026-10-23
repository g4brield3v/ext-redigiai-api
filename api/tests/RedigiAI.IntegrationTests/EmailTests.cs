using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RedigiAI.Application.Abstractions;
using RedigiAI.Application.Essays;
using RedigiAI.Application.Notifications;
using RedigiAI.Domain.Essays;
using RedigiAI.Domain.Jobs;

namespace RedigiAI.IntegrationTests;

/// <summary>Os avisos por e-mail da redação (especificação, seção 10), com o envio ligado no banco.</summary>
[Collection(ApiCollection.Name)]
public class EmailTests(ApiFixture api) : IAsyncLifetime
{
    private const string PromptSlug = "algoritmos-e-o-debate-publico";

    public Task InitializeAsync() => SetSendingAsync(true);

    /// <summary>Os outros testes rodam com o envio desligado, como ele nasce.</summary>
    public Task DisposeAsync() => SetSendingAsync(false);

    [Fact]
    public async Task Sending_turned_off_in_the_database_drops_the_notice_without_sending()
    {
        await SetSendingAsync(false);
        var student = await Student.CreateAsync(api);
        var essayId = await student.SubmitAsync();
        await RunAsync(JobTypes.Transcribe);
        await RunAsync(JobTypes.Grade);

        await RunAsync(JobTypes.SendEmail);

        Assert.Empty(api.Emails.For(essayId));
        Assert.False(await api.QueryAsync(db => db.EmailLog.AnyAsync(e => e.RefId == essayId)));
        var job = (await api.QueryAsync(db => db.Jobs.Where(j => j.Type == JobTypes.SendEmail).ToListAsync()))
            .Single(j => j.Payload.Contains(essayId.ToString()));
        Assert.Equal(JobStatus.Done, job.Status);

        // Ligar depois não reenvia o aviso descartado.
        await SetSendingAsync(true);
        await RunAsync(JobTypes.SendEmail);
        Assert.Empty(api.Emails.For(essayId));
    }

    [Fact]
    public async Task Recent_claim_from_another_process_makes_the_job_try_again_later()
    {
        var student = await Student.CreateAsync(api);
        var essayId = await student.SubmitAsync();
        await RunAsync(JobTypes.Transcribe);
        await RunAsync(JobTypes.Grade);
        // Outro worker reservou o envio agora e pode ter caído no meio.
        await api.QueryAsync(db =>
        {
            db.EmailLog.Add(RedigiAI.Domain.Notifications.EmailLogEntry.Claim(student.Id, EmailTemplates.CorrectionReady, essayId, DateTimeOffset.UtcNow));
            return db.SaveChangesAsync();
        });

        var busy = await Assert.ThrowsAsync<RedigiAI.Domain.Common.AppError>(() => HandleAsync(EmailTemplates.CorrectionReady, essayId));
        Assert.Equal(("EMAIL_BUSY", true), (busy.Code, busy.Retryable));
        Assert.Empty(api.Emails.For(essayId));

        // Passados 10 minutos sem envio, a reserva é retomada e o e-mail sai.
        await api.QueryAsync(db => db.EmailLog.Where(e => e.RefId == essayId)
            .ExecuteUpdateAsync(e => e.SetProperty(x => x.ClaimedAt, DateTimeOffset.UtcNow.AddMinutes(-11))));
        await HandleAsync(EmailTemplates.CorrectionReady, essayId);
        Assert.Single(api.Emails.For(essayId));
    }

    [Fact]
    public async Task Timeout_keeps_the_claim_so_the_email_is_not_sent_twice()
    {
        var student = await Student.CreateAsync(api);
        var essayId = await student.SubmitAsync();
        await RunAsync(JobTypes.Transcribe);
        await RunAsync(JobTypes.Grade);
        // O provedor pode ter aceitado a mensagem antes de a resposta demorar demais.
        api.Emails.Failures.Enqueue(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout."));

        await Assert.ThrowsAsync<TaskCanceledException>(() => HandleAsync(EmailTemplates.CorrectionReady, essayId));

        Assert.NotNull((await api.QueryAsync(db => db.EmailLog.SingleAsync(e => e.RefId == essayId))).ClaimedAt);
        var busy = await Assert.ThrowsAsync<RedigiAI.Domain.Common.AppError>(() => HandleAsync(EmailTemplates.CorrectionReady, essayId));
        Assert.Equal("EMAIL_BUSY", busy.Code);
        Assert.Empty(api.Emails.For(essayId));
    }

    [Fact]
    public async Task Correction_ready_is_sent_once_with_the_link_and_without_the_correction()
    {
        var student = await Student.CreateAsync(api);
        var essayId = await student.SubmitAsync();
        await RunAsync(JobTypes.Transcribe);
        await RunAsync(JobTypes.Grade);

        await RunAsync(JobTypes.SendEmail);
        // O mesmo evento de novo (job repetido) não manda outro e-mail.
        await HandleAsync(EmailTemplates.CorrectionReady, essayId);

        var email = Assert.Single(api.Emails.For(essayId));
        Assert.Equal((EmailTemplates.CorrectionReady, await student.EmailAsync()), (email.Template, email.To));
        var title = await api.QueryAsync(db => db.Essays.Where(e => e.Id == essayId).Select(e => e.Title).SingleAsync());
        Assert.Equal($"Sua correção está pronta: {title}", email.Subject);
        Assert.Contains($"{ApiFixture.WebOrigin}/redacoes/{essayId}\"", email.Html);
        Assert.Contains($"{ApiFixture.WebOrigin}/redacoes/{essayId}", email.Text);
        Assert.Contains("Você pode desligar este aviso", email.Text);

        // Só o aviso: nem a nota (27 de 40) nem o texto da redação ou da correção.
        foreach (var body in new[] { email.Html, email.Text })
        {
            Assert.DoesNotContain("27", body.Replace(essayId.ToString(), ""));
            Assert.DoesNotContain("A tecnologia mudou", body);
        }

        var log = await api.QueryAsync(db => db.EmailLog.SingleAsync(e => e.RefId == essayId));
        Assert.Equal((EmailTemplates.CorrectionReady, student.Id), (log.Template, log.UserId));
        Assert.NotNull(log.SentAt);
        Assert.StartsWith("fake-", log.ProviderMessageId);
    }

    [Fact]
    public async Task Turned_off_correction_ready_is_not_sent()
    {
        var student = await Student.CreateAsync(api);
        var patch = await student.Client.PatchAsJsonAsync("/v1/me", new { emailPreferences = new { correctionReady = false } });
        Assert.True(patch.IsSuccessStatusCode);
        var essayId = await student.SubmitAsync();
        await RunAsync(JobTypes.Transcribe);
        await RunAsync(JobTypes.Grade);

        await RunAsync(JobTypes.SendEmail);

        Assert.Equal(EssayStatus.Completed, await student.StatusAsync(essayId));
        Assert.Empty(api.Emails.For(essayId));
        Assert.False(await api.QueryAsync(db => db.EmailLog.AnyAsync(e => e.RefId == essayId)));
    }

    [Fact]
    public async Task Illegible_words_notice_brings_the_count_the_deadline_and_the_review_link()
    {
        var student = await Student.CreateAsync(api);
        var essayId = await student.SubmitAsync();
        api.Transcriber.Script.Enqueue(() => FakeTranscriber.Reading(lines: ["Os [?] decidem o que [?] vemos."]));
        await RunAsync(JobTypes.Transcribe);

        await RunAsync(JobTypes.SendEmail);

        var email = Assert.Single(api.Emails.For(essayId));
        Assert.Equal(EmailTemplates.IllegibleWords, email.Template);
        Assert.StartsWith("Faltam 2 palavras para corrigir: ", email.Subject);
        Assert.Contains("Não conseguimos ler 2 palavras", email.Text);
        Assert.Contains($"{ApiFixture.WebOrigin}/redacoes/{essayId}/revisao", email.Text);

        // O prazo aparece no horário de Brasília (UTC−3, sem horário de verão).
        var deadline = await api.QueryAsync(db => db.Essays.Where(e => e.Id == essayId).Select(e => e.ReviewDeadlineAt!.Value).SingleAsync());
        Assert.Contains($"O prazo vai até {deadline.ToOffset(TimeSpan.FromHours(-3)):dd/MM} às {deadline.ToOffset(TimeSpan.FromHours(-3)):HH}h", email.Text);
        Assert.DoesNotContain("Você pode desligar este aviso", email.Text);
    }

    [Fact]
    public async Task Reminder_is_scheduled_once_after_12_hours_and_skipped_if_the_words_were_filled()
    {
        var student = await Student.CreateAsync(api);
        var reminded = await student.SubmitAsync();
        var filled = await student.SubmitAsync(extraCredit: true);
        var early = await student.SubmitAsync(extraCredit: true);
        foreach (var _ in new[] { reminded, filled, early })
        {
            api.Transcriber.Script.Enqueue(() => FakeTranscriber.Reading(lines: ["Uma [?] ilegivel aqui."]));
        }

        await RunAsync(JobTypes.Transcribe);
        await RunAsync(JobTypes.SendEmail);

        // Duas já passaram de 12 h do aviso; a terceira ainda não.
        await api.QueryAsync(db => db.Essays.Where(e => e.Id == reminded || e.Id == filled)
            .ExecuteUpdateAsync(e => e.SetProperty(x => x.ReviewDeadlineAt, DateTimeOffset.UtcNow.AddHours(11))));

        await using (var scope = api.Factory.Services.CreateAsyncScope())
        {
            var essays = scope.ServiceProvider.GetRequiredService<IEssayService>();
            Assert.True(await essays.ScheduleReviewRemindersAsync(CancellationToken.None) >= 2);
            await essays.ScheduleReviewRemindersAsync(CancellationToken.None);
        }

        // Antes de o lembrete sair, o aluno preenche as palavras de uma delas.
        var wordId = await api.QueryAsync(db => db.IllegibleWords
            .Where(w => db.Transcriptions.Any(t => t.Id == w.TranscriptionId && t.EssayId == filled)).Select(w => w.Id).SingleAsync());
        var put = await student.Client.PutAsJsonAsync($"/v1/essays/{filled}/illegible", new[] { new { wordId, value = "palavra" } });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        await RunAsync(JobTypes.SendEmail);

        var reminder = Assert.Single(api.Emails.For(reminded), email => email.Template == EmailTemplates.IllegibleReminder);
        Assert.StartsWith("Lembrete: falta 1 palavra na redação ", reminder.Subject);
        Assert.Contains("Você pode desligar este aviso", reminder.Text);
        Assert.DoesNotContain(api.Emails.For(filled), email => email.Template == EmailTemplates.IllegibleReminder);
        Assert.DoesNotContain(api.Emails.For(early), email => email.Template == EmailTemplates.IllegibleReminder);
        var emailJobs = await api.QueryAsync(db => db.Jobs.Where(j => j.Type == JobTypes.SendEmail).ToListAsync());
        Assert.Single(emailJobs, j => j.Payload.Contains(EmailTemplates.IllegibleReminder) && j.Payload.Contains(reminded.ToString()));
    }

    [Fact]
    public async Task Unreadable_photo_notice_uses_the_catalog_text_and_links_to_a_new_submission()
    {
        var student = await Student.CreateAsync(api);
        var essayId = await student.SubmitAsync();
        api.Transcriber.Script.Enqueue(() => FakeTranscriber.Reading(lines: [string.Join(' ', Enumerable.Repeat("[?]", 12))]));
        await RunAsync(JobTypes.Transcribe);

        await RunAsync(JobTypes.SendEmail);

        var email = Assert.Single(api.Emails.For(essayId));
        Assert.Equal(EmailTemplates.PhotoUnreadable, email.Template);
        Assert.StartsWith("Não conseguimos ler a sua foto: ", email.Subject);
        Assert.Contains(
            "Mais de 10 palavras ficaram ilegíveis (12), então a redação não foi corrigida. A correção voltou para o seu saldo.",
            email.Text);
        Assert.Contains($"{ApiFixture.WebOrigin}/propostas/{PromptSlug}/enviar", email.Text);
    }

    [Fact]
    public async Task Failed_transcription_notice_never_blames_the_student()
    {
        var student = await Student.CreateAsync(api);
        var essayId = await student.SubmitAsync();
        api.Transcriber.Script.Enqueue(FakeTranscriber.Invalid);
        api.Transcriber.Script.Enqueue(FakeTranscriber.Invalid);
        await RunAsync(JobTypes.Transcribe);

        await RunAsync(JobTypes.SendEmail);

        var email = Assert.Single(api.Emails.For(essayId));
        Assert.Equal(EmailTemplates.EssayFailed, email.Template);
        Assert.StartsWith("Não conseguimos processar a foto: ", email.Subject);
        Assert.Contains("Houve uma falha do nosso lado ao ler a foto. A correção voltou para o seu saldo.", email.Text);
        Assert.Contains($"{ApiFixture.WebOrigin}/painel", email.Text);
        // O código interno da falha não aparece.
        Assert.DoesNotContain("AI_", email.Html);
    }

    [Fact]
    public async Task Expired_review_notice_is_sent_after_the_cancellation()
    {
        var student = await Student.CreateAsync(api);
        var essayId = await student.SubmitAsync();
        api.Transcriber.Script.Enqueue(() => FakeTranscriber.Reading(lines: ["Uma [?] ilegivel aqui."]));
        await RunAsync(JobTypes.Transcribe);
        await api.QueryAsync(db => db.Essays.Where(e => e.Id == essayId)
            .ExecuteUpdateAsync(e => e.SetProperty(x => x.ReviewDeadlineAt, DateTimeOffset.UtcNow.AddMinutes(-1))));
        await using (var scope = api.Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IEssayService>().CancelExpiredReviewsAsync(CancellationToken.None);
        }

        await RunAsync(JobTypes.SendEmail);

        // O aviso das palavras ilegíveis da manhã não sai mais: a redação já não está em revisão.
        var email = Assert.Single(api.Emails.For(essayId));
        Assert.Equal(EmailTemplates.ReviewExpired, email.Template);
        Assert.StartsWith("O prazo para preencher as palavras acabou: ", email.Subject);
        Assert.Contains(
            "As palavras ilegíveis não foram preenchidas em 24 horas, então a redação foi cancelada. A correção voltou para o seu saldo.",
            email.Text);
    }

    [Fact]
    public async Task Provider_failure_releases_the_claim_and_the_next_attempt_sends_once()
    {
        var student = await Student.CreateAsync(api);
        var essayId = await student.SubmitAsync();
        await RunAsync(JobTypes.Transcribe);
        await RunAsync(JobTypes.Grade);
        api.Emails.Failures.Enqueue(new HttpRequestException("SendGrid unavailable (503)."));

        await Assert.ThrowsAsync<HttpRequestException>(() => HandleAsync(EmailTemplates.CorrectionReady, essayId));
        var released = await api.QueryAsync(db => db.EmailLog.SingleAsync(e => e.RefId == essayId));
        Assert.Null(released.ClaimedAt);
        Assert.Null(released.SentAt);

        await HandleAsync(EmailTemplates.CorrectionReady, essayId);
        await HandleAsync(EmailTemplates.CorrectionReady, essayId);

        Assert.Single(api.Emails.For(essayId));
    }

    [Fact]
    public async Task Two_simultaneous_executions_send_one_email()
    {
        var student = await Student.CreateAsync(api);
        var essayId = await student.SubmitAsync();
        await RunAsync(JobTypes.Transcribe);
        await RunAsync(JobTypes.Grade);

        // Quem encontra a reserva de outra execução ainda enviando sai com EMAIL_BUSY, para o job tentar de novo:
        // isso depende de quanto o envio demora, então só não pode haver outra falha nem um segundo e-mail.
        await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
        {
            try
            {
                await HandleAsync(EmailTemplates.CorrectionReady, essayId);
            }
            catch (RedigiAI.Domain.Common.AppError busy) when (busy.Code == "EMAIL_BUSY")
            {
            }
        }));

        Assert.Single(api.Emails.For(essayId));
        Assert.Equal(1, await api.QueryAsync(db => db.EmailLog.CountAsync(e => e.RefId == essayId)));
    }

    [Fact]
    public async Task Deleted_account_gets_no_email()
    {
        var student = await Student.CreateAsync(api);
        var essayId = await student.SubmitAsync();
        await RunAsync(JobTypes.Transcribe);
        await RunAsync(JobTypes.Grade);
        await api.QueryAsync(db => db.Users.Where(u => u.Id == student.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.DeletedAt, DateTimeOffset.UtcNow)));

        await RunAsync(JobTypes.SendEmail);

        Assert.Empty(api.Emails.For(essayId));
    }

    private Task<int> SetSendingAsync(bool enabled) =>
        api.QueryAsync(db => db.FeatureFlags
            .Where(f => f.Key == RedigiAI.Domain.Common.FeatureFlag.EmailSending)
            .ExecuteUpdateAsync(f => f.SetProperty(x => x.Enabled, enabled)));

    /// <summary>Roda todos os jobs prontos do tipo, como o worker faria.</summary>
    private async Task RunAsync(string type)
    {
        var runner = api.Factory.Services.GetRequiredService<IJobRunner>();
        while (await runner.RunNextAsync([type], CancellationToken.None))
        {
        }
    }

    /// <summary>Executa o handler de e-mail direto, como uma nova execução do mesmo job.</summary>
    private async Task HandleAsync(string template, Guid essayId)
    {
        await using var scope = api.Factory.Services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetServices<IJobHandler>().Single(h => h.Type == JobTypes.SendEmail);
        await handler.HandleAsync(JsonSerializer.Serialize(new EmailJobPayload(template, essayId)), CancellationToken.None);
    }

    private sealed class Student
    {
        private readonly ApiFixture _api;
        private Guid _promptId;

        private Student(ApiFixture api, HttpClient client)
        {
            _api = api;
            Client = client;
        }

        public HttpClient Client { get; }

        public Guid Id { get; private set; }

        public static async Task<Student> CreateAsync(ApiFixture api)
        {
            // Jobs deixados por outros testes rodam antes, com as respostas padrão, para não consumir o roteiro deste.
            var runner = api.Factory.Services.GetRequiredService<IJobRunner>();
            while (await runner.RunNextAsync([JobTypes.Transcribe, JobTypes.Grade], CancellationToken.None))
            {
            }

            var student = new Student(api, api.CreateClient(api.CreateToken(ApiFixture.NewClerkUserId())));
            var profile = await student.Client.PostAsJsonAsync("/v1/me", new { birthDate = "2000-05-17", termsAccepted = true });
            student.Id = (await profile.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            var prompt = await student.Client.GetFromJsonAsync<JsonElement>($"/v1/prompts/{PromptSlug}");
            student._promptId = prompt.GetProperty("id").GetGuid();
            return student;
        }

        public async Task<Guid> SubmitAsync(bool extraCredit = false)
        {
            if (extraCredit)
            {
                await using var scope = _api.Factory.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<RedigiAI.Application.Billing.ICreditService>().GrantAsync(
                    new RedigiAI.Application.Billing.GrantCreditsCommand(Id, RedigiAI.Domain.Billing.CreditSource.Single, 1, null, "test"),
                    CancellationToken.None);
            }

            var assetId = (await (await Client.PostAsync("/v1/uploads/sign", null)).Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("assetId").GetGuid();
            var publicId = await _api.QueryAsync(db => db.Assets.Where(a => a.Id == assetId).Select(a => a.PublicId).SingleAsync());
            _api.Photos.Stored[publicId] = new StoredPhoto(2 * 1024 * 1024, "jpg");

            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/essays/batch")
            {
                Content = JsonContent.Create(new { essays = new[] { new { promptId = _promptId, assetId } }, aiNoticeAccepted = true }),
            };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            var response = await Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("essayIds")[0].GetGuid();
        }

        public Task<EssayStatus> StatusAsync(Guid essayId) =>
            _api.QueryAsync(db => db.Essays.Where(e => e.Id == essayId).Select(e => e.Status).SingleAsync());

        public Task<string> EmailAsync() =>
            _api.QueryAsync(db => db.Users.Where(u => u.Id == Id).Select(u => u.Email).SingleAsync());
    }
}
