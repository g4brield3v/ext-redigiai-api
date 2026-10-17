using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RedigiAI.Application.Abstractions;
using RedigiAI.Application.Billing;
using RedigiAI.Application.Essays;
using RedigiAI.Domain.Billing;
using RedigiAI.Domain.Essays;
using RedigiAI.Domain.Jobs;

namespace RedigiAI.IntegrationTests;

[Collection(ApiCollection.Name)]
public class TranscriptionTests(ApiFixture api)
{
    private static readonly string[] Transcribe = [JobTypes.Transcribe];

    [Fact]
    public async Task Clean_transcription_goes_to_the_grading_queue()
    {
        var student = await Student.CreateAsync(api);
        var essayId = await student.SubmitAsync();

        await RunTranscriptionsAsync();

        Assert.Equal(EssayStatus.Queued, await student.StatusAsync(essayId));
        var transcription = await api.QueryAsync(db => db.Transcriptions.SingleAsync(t => t.EssayId == essayId));
        Assert.Equal((0, FakeTranscriber.Model, false), (transcription.IllegibleCount, transcription.Model, transcription.IdentificationFound));
        Assert.Equal("A tecnologia mudou a forma de debater.", transcription.Lines[0].Text);

        var jobs = (await api.QueryAsync(db => db.Jobs.ToListAsync())).Where(j => j.Payload.Contains(essayId.ToString())).ToList();
        Assert.Equal(JobStatus.Done, jobs.Single(j => j.Type == JobTypes.Transcribe).Status);
        Assert.Equal(JobStatus.Ready, jobs.Single(j => j.Type == JobTypes.Grade).Status);

        var usage = await api.QueryAsync(db => db.AiUsages.SingleAsync(u => u.EssayId == essayId));
        Assert.Equal((AiUsage.Transcription, 5000, 1500), (usage.Stage, usage.InputTokens, usage.OutputTokens));
        // A correção continua reservada: só volta em falha.
        Assert.Equal(0, await student.BalanceAsync());
    }

    [Fact]
    public async Task One_to_ten_illegible_words_wait_for_the_student_and_then_go_to_grading()
    {
        var student = await Student.CreateAsync(api);
        var other = await Student.CreateAsync(api);
        var essayId = await student.SubmitAsync();
        api.Transcriber.Script.Enqueue(() => FakeTranscriber.Reading(lines: ["Os [?] decidem o que vemos", "e isso [?] o debate publico."]));

        await RunTranscriptionsAsync();

        var detail = await student.Client.GetFromJsonAsync<JsonElement>($"/v1/essays/{essayId}");
        Assert.Equal("awaiting_review", detail.GetProperty("status").GetString());
        Assert.Equal(2, detail.GetProperty("illegibleCount").GetInt32());
        var deadline = detail.GetProperty("reviewDeadlineAt").GetDateTimeOffset();
        Assert.InRange(deadline, DateTimeOffset.UtcNow.AddHours(23.9), DateTimeOffset.UtcNow.AddHours(24.1));
        Assert.Equal(
            ["Os [?] decidem o que vemos", "e isso [?] o debate publico."],
            detail.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("text").GetString()));
        var words = detail.GetProperty("illegibleWords").EnumerateArray().ToList();
        Assert.Equal(
            [(1, 3, 6, "Os ", " decidem o que vemos"), (2, 7, 10, "e isso ", " o debate publico.")],
            words.Select(w => (
                w.GetProperty("lineNumber").GetInt32(),
                w.GetProperty("start").GetInt32(),
                w.GetProperty("end").GetInt32(),
                w.GetProperty("contextBefore").GetString()!,
                w.GetProperty("contextAfter").GetString()!)));
        var list = await student.Client.GetFromJsonAsync<JsonElement>("/v1/essays");
        Assert.Equal(2, list.GetProperty("items")[0].GetProperty("illegibleCount").GetInt32());

        var ids = words.Select(w => w.GetProperty("id").GetGuid()).ToList();
        string Path() => $"/v1/essays/{essayId}/illegible";
        await AccountTests.AssertErrorAsync(
            await other.Client.PutAsJsonAsync(Path(), Answers(ids, "algoritmos", "empobrece")), HttpStatusCode.NotFound, "ESSAY_NOT_FOUND");
        await AccountTests.AssertErrorAsync(
            await student.Client.PutAsJsonAsync(Path(), Answers(ids.Take(1), "algoritmos")), HttpStatusCode.UnprocessableEntity, "REVIEW_INCOMPLETE");
        await AccountTests.AssertErrorAsync(
            await student.Client.PutAsJsonAsync(Path(), Answers(ids, "algoritmos", "   ")), HttpStatusCode.UnprocessableEntity, "REVIEW_INCOMPLETE");
        Assert.Equal(EssayStatus.AwaitingReview, await student.StatusAsync(essayId));

        // O que o aluno digita é guardado como digitou, sem correção.
        var filled = await student.Client.PutAsJsonAsync(Path(), Answers(ids, " algorítimos ", "empobrece"));

        Assert.Equal(HttpStatusCode.NoContent, filled.StatusCode);
        Assert.Equal(EssayStatus.Queued, await student.StatusAsync(essayId));
        var stored = await api.QueryAsync(db => db.IllegibleWords.Where(w => ids.Contains(w.Id)).OrderBy(w => w.LineNumber).ToListAsync());
        Assert.Equal(["algorítimos", "empobrece"], stored.Select(w => w.StudentValue));
        Assert.All(stored, word => Assert.NotNull(word.FilledAt));
        Assert.Equal("Os [?] decidem o que vemos", (await api.QueryAsync(db => db.Transcriptions.SingleAsync(t => t.EssayId == essayId))).Lines[0].Text);
        Assert.Single((await api.QueryAsync(db => db.Jobs.Where(j => j.Type == JobTypes.Grade).ToListAsync())), j => j.Payload.Contains(essayId.ToString()));

        await AccountTests.AssertErrorAsync(
            await student.Client.PutAsJsonAsync(Path(), Answers(ids, "a", "b")), HttpStatusCode.Conflict, "INVALID_STATE");
    }

    [Theory]
    [InlineData("eleven-illegible")]
    [InlineData("not-readable")]
    public async Task Unreadable_photo_fails_and_gives_the_credit_back_once(string reason)
    {
        var student = await Student.CreateAsync(api);
        var essayId = await student.SubmitAsync();
        api.Transcriber.Script.Enqueue(() => reason == "not-readable"
            ? FakeTranscriber.Reading(readable: false)
            : FakeTranscriber.Reading(lines: [string.Join(' ', Enumerable.Repeat("[?]", 11))]));

        await RunTranscriptionsAsync();

        var essay = await api.QueryAsync(db => db.Essays.SingleAsync(e => e.Id == essayId));
        Assert.Equal((EssayStatus.Failed, "PHOTO_UNREADABLE"), (essay.Status, essay.FailureCode));
        Assert.Equal(1, await student.BalanceAsync());
        var ledger = await api.QueryAsync(db => db.CreditLedger.Where(e => e.EssayId == essayId).OrderBy(e => e.Id).ToListAsync());
        Assert.Equal([(-1, CreditSource.Trial), (1, CreditSource.Refund)], ledger.Select(e => (e.Delta, e.Source)));
        Assert.False(await api.QueryAsync(db => db.Transcriptions.AnyAsync(t => t.EssayId == essayId)));

        // Rodar de novo o encerramento não devolve outra vez.
        await using var scope = api.Factory.Services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetServices<IJobHandler>().Single(h => h.Type == JobTypes.Transcribe);
        await handler.OnDeadAsync(JsonSerializer.Serialize(new { essayId }), CancellationToken.None);
        Assert.Equal(1, await student.BalanceAsync());
    }

    [Fact]
    public async Task Response_outside_the_format_is_asked_once_more_and_then_fails_for_good()
    {
        var student = await Student.CreateAsync(api);
        var essayId = await student.SubmitAsync();
        api.Transcriber.Script.Enqueue(FakeTranscriber.Invalid);
        api.Transcriber.Script.Enqueue(() => FakeTranscriber.Reading(lines: ["texto"]) with
        {
            Reading = new TranscriptionReading([new ReadLine(31, "linha fora da folha", false)], false, true),
        });

        await RunTranscriptionsAsync();

        var essay = await api.QueryAsync(db => db.Essays.SingleAsync(e => e.Id == essayId));
        Assert.Equal((EssayStatus.Failed, "TRANSCRIPTION_FAILED"), (essay.Status, essay.FailureCode));
        var job = (await api.QueryAsync(db => db.Jobs.Where(j => j.Type == JobTypes.Transcribe).ToListAsync()))
            .Single(j => j.Payload.Contains(essayId.ToString()));
        Assert.Equal((JobStatus.Dead, 1, "AI_INVALID_RESPONSE"), (job.Status, job.Attempts, job.LastError));
        Assert.Equal(2, await api.QueryAsync(db => db.AiUsages.CountAsync(u => u.EssayId == essayId)));
        Assert.Equal(1, await student.BalanceAsync());
    }

    [Fact]
    public async Task Unavailable_ai_keeps_the_essay_and_the_credit_for_the_next_attempt()
    {
        var student = await Student.CreateAsync(api);
        var essayId = await student.SubmitAsync();
        api.Transcriber.Script.Enqueue(FakeTranscriber.Unavailable);

        await RunTranscriptionsAsync();

        Assert.Equal(EssayStatus.Uploaded, await student.StatusAsync(essayId));
        Assert.Equal(0, await student.BalanceAsync());
        var job = (await api.QueryAsync(db => db.Jobs.Where(j => j.Type == JobTypes.Transcribe).ToListAsync()))
            .Single(j => j.Payload.Contains(essayId.ToString()));
        Assert.Equal((JobStatus.Ready, 1, "AI_UNAVAILABLE"), (job.Status, job.Attempts, job.LastError));
        Assert.True(job.RunAt > DateTimeOffset.UtcNow.AddSeconds(20));

        // Na hora da nova tentativa, com a IA de volta, a transcrição sai.
        await api.QueryAsync(db => db.Jobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(j => j.SetProperty(x => x.RunAt, DateTimeOffset.UtcNow)));
        await RunTranscriptionsAsync();
        Assert.Equal(EssayStatus.Queued, await student.StatusAsync(essayId));
    }

    [Fact]
    public async Task Review_after_the_deadline_cancels_the_essay_and_gives_the_credit_back_once()
    {
        var student = await Student.CreateAsync(api);
        var essayId = await student.SubmitAsync();
        var swept = await student.SubmitAsync(extraCredit: true);
        api.Transcriber.Script.Enqueue(() => FakeTranscriber.Reading(lines: ["Uma [?] ilegivel."]));
        api.Transcriber.Script.Enqueue(() => FakeTranscriber.Reading(lines: ["Outra [?] ilegivel."]));
        await RunTranscriptionsAsync();
        var wordId = await api.QueryAsync(db => db.IllegibleWords
            .Where(w => db.Transcriptions.Any(t => t.Id == w.TranscriptionId && t.EssayId == essayId)).Select(w => w.Id).SingleAsync());
        await api.QueryAsync(db => db.Essays.Where(e => e.Id == essayId || e.Id == swept)
            .ExecuteUpdateAsync(e => e.SetProperty(x => x.ReviewDeadlineAt, DateTimeOffset.UtcNow.AddMinutes(-1))));

        // O aluno tenta preencher depois do prazo, antes de a varredura passar.
        var late = await student.Client.PutAsJsonAsync($"/v1/essays/{essayId}/illegible", Answers([wordId], "palavra"));

        var error = await AccountTests.AssertErrorAsync(late, HttpStatusCode.Conflict, "REVIEW_EXPIRED");
        Assert.Contains("voltou para o seu saldo", error.GetProperty("message").GetString());
        var cancelled = await api.QueryAsync(db => db.Essays.SingleAsync(e => e.Id == essayId));
        Assert.Equal((EssayStatus.Cancelled, "REVIEW_EXPIRED"), (cancelled.Status, cancelled.FailureCode));
        Assert.Equal(1, await student.BalanceAsync());

        // A varredura cancela a outra e não mexe de novo na que já foi cancelada.
        await using var scope = api.Factory.Services.CreateAsyncScope();
        var essays = scope.ServiceProvider.GetRequiredService<IEssayService>();
        Assert.True(await essays.CancelExpiredReviewsAsync(CancellationToken.None) >= 1);
        await essays.CancelExpiredReviewsAsync(CancellationToken.None);

        Assert.Equal(EssayStatus.Cancelled, await student.StatusAsync(swept));
        Assert.Equal(2, await student.BalanceAsync());
        Assert.Equal(2, await api.QueryAsync(db => db.CreditLedger.CountAsync(e => e.UserId == student.Id && e.Source == CreditSource.Refund)));
    }

    [Fact]
    public async Task Credit_from_an_expired_plan_comes_back_as_a_single_credit()
    {
        var student = await Student.CreateAsync(api);
        await student.GrantAsync(CreditSource.PlanWeek, 1, DateTimeOffset.UtcNow.AddDays(3));
        var essayId = await student.SubmitAsync();
        var planBucket = await api.QueryAsync(db => db.CreditLedger.Where(e => e.EssayId == essayId).Select(e => e.BucketId).SingleAsync());
        await api.QueryAsync(db => db.CreditBuckets.Where(b => b.Id == planBucket)
            .ExecuteUpdateAsync(b => b.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1))));
        api.Transcriber.Script.Enqueue(() => FakeTranscriber.Reading(readable: false));

        await RunTranscriptionsAsync();

        var buckets = await api.QueryAsync(db => db.CreditBuckets.Where(b => b.UserId == student.Id).ToListAsync());
        Assert.Equal(0, buckets.Single(b => b.Id == planBucket).Remaining);
        var refund = buckets.Single(b => b.Source == CreditSource.Single);
        Assert.Equal((1, (DateTimeOffset?)null), (refund.Remaining, refund.ExpiresAt));
        // O teste grátis continua lá, mais a correção devolvida.
        Assert.Equal(2, await student.BalanceAsync());
    }

    [Fact]
    public async Task Another_attempt_that_does_not_fit_the_hard_cost_cap_is_not_made()
    {
        await using var factory = api.CreateFactory(builder =>
        {
            builder.UseSetting($"Ai:Pricing:{FakeTranscriber.Model}:Input", "3");
            builder.UseSetting($"Ai:Pricing:{FakeTranscriber.Model}:Output", "15");
            builder.UseSetting("Ai:HardCapUsd", "0.02");
        });
        var student = await Student.CreateAsync(api);
        var essayId = await student.SubmitAsync();
        await RunTranscriptionsAsync(skip: essayId);
        api.Transcriber.Script.Enqueue(FakeTranscriber.Invalid);

        await RunTranscriptionsAsync(factory);

        // 5.000 tokens de entrada a US$ 3 e 100 de saída a US$ 15 por milhão = US$ 0,0165; duas chamadas passariam de US$ 0,02.
        var usage = await api.QueryAsync(db => db.AiUsages.SingleAsync(u => u.EssayId == essayId));
        Assert.Equal(16_500, usage.CostMicroUsd);
        var job = (await api.QueryAsync(db => db.Jobs.Where(j => j.Type == JobTypes.Transcribe).ToListAsync()))
            .Single(j => j.Payload.Contains(essayId.ToString()));
        Assert.Equal((JobStatus.Dead, "AI_BUDGET_EXCEEDED"), (job.Status, job.LastError));
        Assert.Equal(EssayStatus.Failed, await student.StatusAsync(essayId));
        Assert.Equal(1, await student.BalanceAsync());
    }

    [Fact]
    public async Task Photo_link_is_only_for_the_owner()
    {
        var student = await Student.CreateAsync(api);
        var other = await Student.CreateAsync(api);
        var essayId = await student.SubmitAsync();

        var photo = await student.Client.GetFromJsonAsync<JsonElement>($"/v1/essays/{essayId}/photo");

        Assert.StartsWith("https://api.cloudinary.com/", photo.GetProperty("url").GetString());
        await AccountTests.AssertErrorAsync(
            await other.Client.GetAsync($"/v1/essays/{essayId}/photo"), HttpStatusCode.NotFound, "ESSAY_NOT_FOUND");
    }

    private static object[] Answers(IEnumerable<Guid> wordIds, params string[] values) =>
        [.. wordIds.Select((wordId, index) => new { wordId, value = values[index] })];

    /// <summary>Roda todos os jobs de transcrição prontos, como o worker faria.</summary>
    /// <param name="skip">Deixa o job desta redação para depois (ele é adiado, não executado).</param>
    private async Task RunTranscriptionsAsync(WebApplicationFactory<Program>? factory = null, Guid? skip = null)
    {
        if (skip is { } essayId)
        {
            var later = DateTimeOffset.UtcNow.AddHours(1);
            var postponed = (await api.QueryAsync(db => db.Jobs.Where(j => j.Type == JobTypes.Transcribe && j.Status == JobStatus.Ready).ToListAsync()))
                .Where(j => j.Payload.Contains(essayId.ToString())).Select(j => j.Id).ToList();
            await api.QueryAsync(db => db.Jobs.Where(j => postponed.Contains(j.Id)).ExecuteUpdateAsync(j => j.SetProperty(x => x.RunAt, later)));
            await DrainAsync(api.Factory);
            await api.QueryAsync(db => db.Jobs.Where(j => postponed.Contains(j.Id)).ExecuteUpdateAsync(j => j.SetProperty(x => x.RunAt, DateTimeOffset.UtcNow)));
            return;
        }

        await DrainAsync(factory ?? api.Factory);
    }

    private static async Task DrainAsync(WebApplicationFactory<Program> factory)
    {
        var runner = factory.Services.GetRequiredService<IJobRunner>();
        while (await runner.RunNextAsync(Transcribe, CancellationToken.None))
        {
        }
    }

    private sealed record Item(Guid PromptId, Guid AssetId);

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
            // Jobs deixados por outros testes rodam antes, com a leitura padrão, para não consumir o roteiro deste.
            var runner = api.Factory.Services.GetRequiredService<IJobRunner>();
            while (await runner.RunNextAsync(Transcribe, CancellationToken.None))
            {
            }

            var student = new Student(api, api.CreateClient(api.CreateToken(ApiFixture.NewClerkUserId())));
            var profile = await student.Client.PostAsJsonAsync("/v1/me", new { birthDate = "2000-05-17", termsAccepted = true });
            student.Id = (await profile.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            var prompt = await student.Client.GetFromJsonAsync<JsonElement>("/v1/prompts/algoritmos-e-o-debate-publico");
            student._promptId = prompt.GetProperty("id").GetGuid();
            return student;
        }

        public async Task GrantAsync(CreditSource source, int amount, DateTimeOffset? expiresAt)
        {
            await using var scope = _api.Factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ICreditService>()
                .GrantAsync(new GrantCreditsCommand(Id, source, amount, expiresAt, "test"), CancellationToken.None);
        }

        public async Task<Guid> SubmitAsync(bool extraCredit = false)
        {
            if (extraCredit)
            {
                await GrantAsync(CreditSource.Single, 1, expiresAt: null);
            }

            var assetId = (await (await Client.PostAsync("/v1/uploads/sign", null)).Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("assetId").GetGuid();
            var publicId = await _api.QueryAsync(db => db.Assets.Where(a => a.Id == assetId).Select(a => a.PublicId).SingleAsync());
            _api.Photos.Stored[publicId] = new StoredPhoto(2 * 1024 * 1024, "jpg");

            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/essays/batch")
            {
                Content = JsonContent.Create(new { essays = new[] { new Item(_promptId, assetId) }, aiNoticeAccepted = true }),
            };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            var response = await Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("essayIds")[0].GetGuid();
        }

        public Task<EssayStatus> StatusAsync(Guid essayId) =>
            _api.QueryAsync(db => db.Essays.Where(e => e.Id == essayId).Select(e => e.Status).SingleAsync());

        public async Task<int> BalanceAsync() =>
            (await Client.GetFromJsonAsync<JsonElement>("/v1/me")).GetProperty("credits").GetProperty("total").GetInt32();
    }
}
