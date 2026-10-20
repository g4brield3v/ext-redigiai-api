using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RedigiAI.Application.Abstractions;
using RedigiAI.Application.Billing;
using RedigiAI.Domain.Billing;
using RedigiAI.Domain.Essays;
using RedigiAI.Domain.Jobs;

namespace RedigiAI.IntegrationTests;

[Collection(ApiCollection.Name)]
public class EssayTests(ApiFixture api)
{
    private const long OneMegabyte = 1024 * 1024;

    [Fact]
    public async Task Upload_signature_reserves_a_private_file_for_the_student()
    {
        var student = await Student.CreateAsync(api);

        var signature = await student.SignAsync();

        Assert.StartsWith("https://api.cloudinary.com/", signature.GetProperty("uploadUrl").GetString());
        Assert.False(string.IsNullOrEmpty(signature.GetProperty("fields").GetProperty("signature").GetString()));
        var assetId = signature.GetProperty("assetId").GetGuid();
        var asset = await api.QueryAsync(db => db.Assets.SingleAsync(a => a.Id == assetId));
        Assert.Equal(student.Id, asset.UserId);
        Assert.False(asset.IsConfirmed);
        Assert.NotNull(asset.PurgeAfter);
    }

    [Fact]
    public async Task Submitting_reserves_the_credit_and_queues_the_transcription_together()
    {
        var student = await Student.CreateAsync(api);
        var assetId = await student.UploadAsync();

        var response = await student.SubmitAsync([student.Item(assetId)]);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var essayId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("essayIds")[0].GetGuid();

        var essay = await api.QueryAsync(db => db.Essays.SingleAsync(e => e.Id == essayId));
        Assert.Equal(EssayStatus.Uploaded, essay.Status);
        Assert.Equal("Fuvest – Dissertação em prosa – Algoritmos e o debate público", essay.Title);
        Assert.Equal((1, (Guid?)null), (essay.Version, essay.ParentEssayId));

        var entry = await api.QueryAsync(db => db.CreditLedger.SingleAsync(e => e.EssayId == essayId));
        Assert.Equal((-1, CreditSource.Trial), (entry.Delta, entry.Source));
        Assert.Equal(0, await student.BalanceAsync());

        var job = (await api.QueryAsync(db => db.Jobs.ToListAsync())).Single(j => j.Payload.Contains(essayId.ToString()));
        Assert.Equal((JobTypes.Transcribe, JobStatus.Ready, 4), (job.Type, job.Status, job.MaxAttempts));

        var asset = await api.QueryAsync(db => db.Assets.SingleAsync(a => a.Id == assetId));
        Assert.True(asset.IsConfirmed);
        Assert.Null(asset.PurgeAfter);
        Assert.True((await student.Client.GetFromJsonAsync<JsonElement>("/v1/me")).GetProperty("aiNoticeAccepted").GetBoolean());
    }

    [Fact]
    public async Task Idempotency_key_is_required_and_repeating_it_creates_nothing_new()
    {
        var student = await Student.CreateAsync(api, extraCredits: 2);
        var assetId = await student.UploadAsync();

        var missing = await student.SubmitAsync([student.Item(assetId)], key: null);
        await AccountTests.AssertErrorAsync(missing, HttpStatusCode.UnprocessableEntity, "VALIDATION_ERROR");

        var key = Guid.NewGuid().ToString();
        var first = await student.SubmitAsync([student.Item(assetId)], key);
        var second = await student.SubmitAsync([student.Item(assetId)], key);

        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        Assert.Equal(1, await api.QueryAsync(db => db.Essays.CountAsync(e => e.UserId == student.Id)));
        Assert.Equal(2, await student.BalanceAsync());
    }

    [Fact]
    public async Task Two_simultaneous_submissions_with_one_credit_let_only_one_through()
    {
        var student = await Student.CreateAsync(api);
        var assets = new[] { await student.UploadAsync(), await student.UploadAsync() };

        var responses = await Task.WhenAll(assets.Select(asset => student.SubmitAsync([student.Item(asset)])));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Accepted);
        await AccountTests.AssertErrorAsync(
            responses.Single(r => r.StatusCode != HttpStatusCode.Accepted), HttpStatusCode.PaymentRequired, "QUOTA_EXCEEDED");
        Assert.Equal(1, await api.QueryAsync(db => db.Essays.CountAsync(e => e.UserId == student.Id)));
        Assert.Equal(1, await api.QueryAsync(db => db.CreditLedger.CountAsync(e => e.UserId == student.Id && e.Delta < 0)));
        Assert.Equal(0, await student.BalanceAsync());
    }

    [Fact]
    public async Task Twenty_simultaneous_submissions_never_take_the_balance_below_zero()
    {
        // Aceite da etapa 4: 6 correções em três origens (plano, teste grátis e avulsa) e 20 envios ao mesmo tempo.
        var student = await Student.CreateAsync(api);
        await student.GrantAsync(CreditSource.Plan, 3, DateTimeOffset.UtcNow.AddDays(10));
        await student.GrantAsync(CreditSource.Single, 2, expiresAt: null);
        var assets = new List<Guid>();
        for (var i = 0; i < 20; i++)
        {
            assets.Add(await student.UploadAsync());
        }

        var responses = await Task.WhenAll(assets.Select(asset => student.SubmitAsync([student.Item(asset)])));

        Assert.Equal(6, responses.Count(r => r.StatusCode == HttpStatusCode.Accepted));
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Accepted), r => Assert.Equal(HttpStatusCode.PaymentRequired, r.StatusCode));
        Assert.Equal(6, await api.QueryAsync(db => db.Essays.CountAsync(e => e.UserId == student.Id)));
        Assert.Equal(0, await student.BalanceAsync());
        // Nenhum bucket abaixo de zero, e o ledger fecha com o saldo de cada um.
        var buckets = await api.QueryAsync(db => db.CreditBuckets.Where(b => b.UserId == student.Id).ToListAsync());
        Assert.All(buckets, b => Assert.True(b.Remaining >= 0));
        foreach (var bucket in buckets)
        {
            var sum = await api.QueryAsync(db => db.CreditLedger.Where(e => e.BucketId == bucket.Id).SumAsync(e => e.Delta));
            Assert.Equal(bucket.Remaining, sum);
        }

        Assert.Equal(6, await api.QueryAsync(db => db.CreditLedger.CountAsync(e => e.UserId == student.Id && e.Delta < 0)));
    }

    [Fact]
    public async Task Batch_is_all_or_nothing_when_the_balance_does_not_cover_it()
    {
        var student = await Student.CreateAsync(api);
        var assets = new[] { await student.UploadAsync(), await student.UploadAsync() };

        var response = await student.SubmitAsync([.. assets.Select(student.Item)]);

        await AccountTests.AssertErrorAsync(response, HttpStatusCode.PaymentRequired, "QUOTA_EXCEEDED");
        Assert.Equal(0, await api.QueryAsync(db => db.Essays.CountAsync(e => e.UserId == student.Id)));
        Assert.Equal(1, await student.BalanceAsync());
        Assert.All(
            await api.QueryAsync(db => db.Assets.Where(a => a.UserId == student.Id).ToListAsync()),
            asset => Assert.False(asset.IsConfirmed));
    }

    [Fact]
    public async Task Batch_spends_the_plan_first_then_the_trial_then_the_single_credits()
    {
        var student = await Student.CreateAsync(api, extraCredits: 1);
        await student.GrantAsync(CreditSource.Plan, 1, DateTimeOffset.UtcNow.AddDays(10));
        var assets = new[] { await student.UploadAsync(), await student.UploadAsync(), await student.UploadAsync() };

        var response = await student.SubmitAsync([.. assets.Select(student.Item)]);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var ids = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("essayIds").EnumerateArray().Select(id => id.GetGuid()).ToList();
        var spent = await api.QueryAsync(db => db.CreditLedger.Where(e => e.EssayId != null && ids.Contains(e.EssayId.Value)).ToListAsync());
        Assert.Equal(
            [CreditSource.Plan, CreditSource.Trial, CreditSource.Single],
            ids.Select(id => spent.Single(e => e.EssayId == id).Source));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("png")]
    [InlineData("too-big")]
    [InlineData("someone-elses")]
    [InlineData("already-used")]
    [InlineData("twice-in-the-batch")]
    public async Task Photo_that_is_not_a_valid_jpg_of_the_student_is_refused(string problem)
    {
        var student = await Student.CreateAsync(api, extraCredits: 3);
        var other = await Student.CreateAsync(api);
        var assetId = problem switch
        {
            "missing" => (await student.SignAsync()).GetProperty("assetId").GetGuid(),
            "png" => await student.UploadAsync(format: "png"),
            "too-big" => await student.UploadAsync(bytes: 10 * OneMegabyte + 1),
            "someone-elses" => await other.UploadAsync(),
            _ => await student.UploadAsync(),
        };
        if (problem == "already-used")
        {
            Assert.Equal(HttpStatusCode.Accepted, (await student.SubmitAsync([student.Item(assetId)])).StatusCode);
        }

        var items = problem == "twice-in-the-batch"
            ? new[] { student.Item(assetId), student.Item(assetId) }
            : [student.Item(assetId)];
        var before = await student.BalanceAsync();

        var response = await student.SubmitAsync(items);

        await AccountTests.AssertErrorAsync(response, HttpStatusCode.UnprocessableEntity, "INVALID_FILE");
        Assert.Equal(before, await student.BalanceAsync());
    }

    [Fact]
    public async Task Prompt_and_ai_notice_are_validated()
    {
        var student = await Student.CreateAsync(api);
        var assetId = await student.UploadAsync();

        await AccountTests.AssertErrorAsync(
            await student.SubmitAsync([student.Item(assetId) with { PromptId = Guid.CreateVersion7() }]),
            HttpStatusCode.NotFound,
            "PROMPT_NOT_AVAILABLE");
        await AccountTests.AssertErrorAsync(
            await student.SubmitAsync([student.Item(assetId)], aiNoticeAccepted: false),
            HttpStatusCode.UnprocessableEntity,
            "VALIDATION_ERROR");
        await AccountTests.AssertErrorAsync(
            await student.SubmitAsync([]), HttpStatusCode.UnprocessableEntity, "VALIDATION_ERROR");
        Assert.Equal(1, await student.BalanceAsync());
    }

    [Fact]
    public async Task Rewrite_becomes_the_next_version_of_the_students_own_essay()
    {
        var student = await Student.CreateAsync(api, extraCredits: 1);
        var other = await Student.CreateAsync(api);
        var firstId = await student.SubmitOneAsync();
        var othersId = await other.SubmitOneAsync();

        var foreign = await student.SubmitAsync([student.Item(await student.UploadAsync()) with { ParentEssayId = othersId }]);
        await AccountTests.AssertErrorAsync(foreign, HttpStatusCode.NotFound, "ESSAY_NOT_FOUND");

        var rewrite = await student.SubmitAsync([student.Item(await student.UploadAsync()) with { ParentEssayId = firstId }]);
        Assert.Equal(HttpStatusCode.Accepted, rewrite.StatusCode);

        var list = (await student.Client.GetFromJsonAsync<JsonElement>("/v1/essays")).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal([2, 1], list.Select(e => e.GetProperty("version").GetInt32()));
        Assert.All(list, e => Assert.True(e.GetProperty("hasVersions").GetBoolean()));
    }

    [Fact]
    public async Task Student_sees_only_their_own_essays()
    {
        var student = await Student.CreateAsync(api);
        var other = await Student.CreateAsync(api);
        var essayId = await student.SubmitOneAsync();

        var list = await student.Client.GetFromJsonAsync<JsonElement>("/v1/essays?status=uploaded");
        var item = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(essayId, item.GetProperty("id").GetGuid());
        Assert.Equal("uploaded", item.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, list.GetProperty("nextCursor").ValueKind);
        Assert.Equal(0, (await student.Client.GetFromJsonAsync<JsonElement>("/v1/essays?status=completed")).GetProperty("items").GetArrayLength());

        var detail = await student.Client.GetFromJsonAsync<JsonElement>($"/v1/essays/{essayId}");
        Assert.Equal("Fuvest – Dissertação em prosa – Algoritmos e o debate público", detail.GetProperty("title").GetString());
        Assert.Equal(30, detail.GetProperty("sheetLines").GetInt32());

        Assert.Equal(0, (await other.Client.GetFromJsonAsync<JsonElement>("/v1/essays")).GetProperty("items").GetArrayLength());
        await AccountTests.AssertErrorAsync(
            await other.Client.GetAsync($"/v1/essays/{essayId}"), HttpStatusCode.NotFound, "ESSAY_NOT_FOUND");
        await AccountTests.AssertErrorAsync(
            await student.Client.GetAsync("/v1/essays?status=inventado"), HttpStatusCode.UnprocessableEntity, "VALIDATION_ERROR");
        await AccountTests.AssertErrorAsync(
            await student.Client.GetAsync("/v1/essays?cursor=abc"), HttpStatusCode.UnprocessableEntity, "VALIDATION_ERROR");
    }

    [Fact]
    public async Task List_is_paged_by_cursor()
    {
        var student = await Student.CreateAsync(api, extraCredits: 21);
        var sent = new List<Guid>();
        for (var batch = 0; batch < 3; batch++)
        {
            var assets = new List<Guid>();
            for (var i = 0; i < 7; i++)
            {
                assets.Add(await student.UploadAsync());
            }

            var response = await student.SubmitAsync([.. assets.Select(student.Item)]);
            sent.AddRange((await response.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("essayIds").EnumerateArray().Select(id => id.GetGuid()));
        }

        var first = await student.Client.GetFromJsonAsync<JsonElement>("/v1/essays");
        var second = await student.Client.GetFromJsonAsync<JsonElement>(
            $"/v1/essays?cursor={first.GetProperty("nextCursor").GetString()}");

        var listed = first.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray())
            .Select(e => e.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(20, first.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
        Assert.Equal(sent.OrderByDescending(id => id).ToList(), listed);
    }

    private sealed record Item(Guid PromptId, Guid AssetId, Guid? ParentEssayId = null);

    /// <summary>Um aluno cadastrado, com o teste grátis e, se pedido, correções avulsas.</summary>
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

        public static async Task<Student> CreateAsync(ApiFixture api, int extraCredits = 0)
        {
            var student = new Student(api, api.CreateClient(api.CreateToken(ApiFixture.NewClerkUserId())));
            var profile = await student.Client.PostAsJsonAsync("/v1/me", new { birthDate = "2000-05-17", termsAccepted = true });
            student.Id = (await profile.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

            var prompt = await student.Client.GetFromJsonAsync<JsonElement>("/v1/prompts/algoritmos-e-o-debate-publico");
            student._promptId = prompt.GetProperty("id").GetGuid();

            if (extraCredits > 0)
            {
                await student.GrantAsync(CreditSource.Single, extraCredits, expiresAt: null);
            }

            return student;
        }

        public async Task GrantAsync(CreditSource source, int amount, DateTimeOffset? expiresAt)
        {
            await using var scope = _api.Factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ICreditService>()
                .GrantAsync(new GrantCreditsCommand(Id, source, amount, expiresAt, "test"), CancellationToken.None);
        }

        public Item Item(Guid assetId) => new(_promptId, assetId);

        public async Task<JsonElement> SignAsync() =>
            await (await Client.PostAsync("/v1/uploads/sign", null)).Content.ReadFromJsonAsync<JsonElement>();

        /// <summary>Pede a assinatura e simula a chegada do arquivo ao provedor.</summary>
        public async Task<Guid> UploadAsync(string format = "jpg", long bytes = 2 * OneMegabyte)
        {
            var assetId = (await SignAsync()).GetProperty("assetId").GetGuid();
            var publicId = await _api.QueryAsync(db => db.Assets.Where(a => a.Id == assetId).Select(a => a.PublicId).SingleAsync());
            _api.Photos.Stored[publicId] = new StoredPhoto(bytes, format);
            return assetId;
        }

        public async Task<HttpResponseMessage> SubmitAsync(Item[] items, string? key = "auto", bool aiNoticeAccepted = true)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/essays/batch")
            {
                Content = JsonContent.Create(new { essays = items, aiNoticeAccepted }),
            };
            if (key is not null)
            {
                request.Headers.Add("Idempotency-Key", key == "auto" ? Guid.NewGuid().ToString() : key);
            }

            return await Client.SendAsync(request);
        }

        public async Task<Guid> SubmitOneAsync()
        {
            var response = await SubmitAsync([Item(await UploadAsync())]);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("essayIds")[0].GetGuid();
        }

        public async Task<int> BalanceAsync() =>
            (await Client.GetFromJsonAsync<JsonElement>("/v1/me")).GetProperty("credits").GetProperty("total").GetInt32();
    }
}
