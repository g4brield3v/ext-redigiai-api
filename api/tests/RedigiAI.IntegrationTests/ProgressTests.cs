using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Essays;
using RedigiAI.Domain.Jobs;

namespace RedigiAI.IntegrationTests;

/// <summary>Painel de progresso (<c>GET /v1/me/progress</c>; especificação, seção 6).</summary>
[Collection(ApiCollection.Name)]
public class ProgressTests(ApiFixture api)
{
    private const string PromptSlug = "algoritmos-e-o-debate-publico";

    [Fact]
    public async Task Shows_the_scores_in_order_the_last_delta_and_the_focus()
    {
        var student = await RegisterAsync();

        // 27 de 40 (níveis 3, 3, 2 e 3): perde 3, 2, 6 e 2 pontos.
        var first = await GradedAsync(student, FakeGrader.Good());
        // 36 de 40 (níveis 4, 4, 4 e 2): perde só 4 pontos no critério 4.
        var second = await GradedAsync(student, Levels(4, 4, 4, 2, mainIssue: 4));
        // Nota zero: entra no gráfico, mas não no foco.
        var third = await GradedAsync(student, FakeGrader.Zero("theme_not_addressed"));

        var progress = await student.GetFromJsonAsync<JsonElement>("/v1/me/progress");

        var points = progress.GetProperty("points").EnumerateArray().ToList();
        Assert.Equal([first, second, third], points.Select(p => p.GetProperty("essayId").GetGuid()));
        Assert.Equal([27, 36, 0], points.Select(p => p.GetProperty("score").GetInt32()));
        Assert.All(points, p => Assert.Equal(40, p.GetProperty("maxScore").GetInt32()));
        Assert.Equal(-36, progress.GetProperty("lastDelta").GetInt32());

        // Nas duas sem nota zero: critério 3 perdeu 6, critério 4 perdeu 2 + 4 = 6. No empate, o de maior peso (3).
        // A orientação vem da mais recente em que o critério 3 perdeu pontos: a primeira, não a segunda.
        var focus = progress.GetProperty("focus");
        Assert.Equal(3, focus.GetProperty("criterionPosition").GetInt32());
        Assert.Equal("Coesão e progressão", focus.GetProperty("criterionName").GetString());
        Assert.Equal("Reescreva o trecho da linha 1 com um dado da coletânea.", focus.GetProperty("guidance").GetString());
        Assert.Equal(first, focus.GetProperty("essayId").GetGuid());
    }

    [Fact]
    public async Task Without_corrections_the_panel_is_empty()
    {
        var student = await RegisterAsync();

        var progress = await student.GetFromJsonAsync<JsonElement>("/v1/me/progress");

        Assert.Empty(progress.GetProperty("points").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, progress.GetProperty("lastDelta").ValueKind);
        Assert.Equal(JsonValueKind.Null, progress.GetProperty("focus").ValueKind);
    }

    [Fact]
    public async Task Only_zero_scores_have_points_but_no_focus()
    {
        var student = await RegisterAsync();
        var essay = await GradedAsync(student, FakeGrader.Zero("theme_not_addressed"));

        var progress = await student.GetFromJsonAsync<JsonElement>("/v1/me/progress");

        Assert.Equal(essay, Assert.Single(progress.GetProperty("points").EnumerateArray()).GetProperty("essayId").GetGuid());
        Assert.Equal(JsonValueKind.Null, progress.GetProperty("lastDelta").ValueKind);
        Assert.Equal(JsonValueKind.Null, progress.GetProperty("focus").ValueKind);
    }

    [Fact]
    public async Task Another_students_corrections_never_appear()
    {
        var other = await RegisterAsync();
        await GradedAsync(other, FakeGrader.Good());
        var student = await RegisterAsync();

        var progress = await student.GetFromJsonAsync<JsonElement>("/v1/me/progress");

        Assert.Empty(progress.GetProperty("points").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, progress.GetProperty("focus").ValueKind);
    }

    [Fact]
    public async Task Requires_login_and_a_finished_registration()
    {
        await AccountTests.AssertErrorAsync(await api.CreateClient().GetAsync("/v1/me/progress"), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");

        var unregistered = api.CreateClient(api.CreateToken(ApiFixture.NewClerkUserId()));
        await AccountTests.AssertErrorAsync(await unregistered.GetAsync("/v1/me/progress"), HttpStatusCode.NotFound, "PROFILE_NOT_FOUND");
    }

    /// <summary>Correção válida com os níveis dados e o problema principal no critério informado.</summary>
    private static JsonObject Levels(int c1, int c2, int c3, int c4, int mainIssue)
    {
        var response = FakeGrader.Good();
        response["criteria"] = new JsonArray(
            FakeGrader.Criterion(1, c1), FakeGrader.Criterion(2, c2), FakeGrader.Criterion(3, c3), FakeGrader.Criterion(4, c4));
        response["main_issue"] = FakeGrader.Point("Corrija a crase da linha 1.", mainIssue);
        response["next_steps"] = new JsonArray(FakeGrader.Point("Corrija a crase da linha 1.", mainIssue));
        return response;
    }

    private async Task<HttpClient> RegisterAsync()
    {
        // Jobs deixados por outros testes rodam antes, com as respostas padrão, para não consumir o roteiro deste.
        await RunAsync(JobTypes.Transcribe, JobTypes.Grade);

        var client = api.CreateClient(api.CreateToken(ApiFixture.NewClerkUserId()));
        (await client.PostAsJsonAsync("/v1/me", new { birthDate = "2000-05-17", termsAccepted = true })).EnsureSuccessStatusCode();
        return client;
    }

    /// <summary>Envia uma redação e a leva até <c>completed</c> com a resposta dada à correção.</summary>
    private async Task<Guid> GradedAsync(HttpClient student, JsonObject correction)
    {
        var userId = (await student.GetFromJsonAsync<JsonElement>("/v1/me")).GetProperty("id").GetGuid();
        await using (var scope = api.Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<RedigiAI.Application.Billing.ICreditService>().GrantAsync(
                new RedigiAI.Application.Billing.GrantCreditsCommand(userId, RedigiAI.Domain.Billing.CreditSource.Single, 1, null, "test"),
                CancellationToken.None);
        }

        var promptId = (await student.GetFromJsonAsync<JsonElement>($"/v1/prompts/{PromptSlug}")).GetProperty("id").GetGuid();
        var assetId = (await (await student.PostAsync("/v1/uploads/sign", null)).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("assetId").GetGuid();
        var publicId = await api.QueryAsync(db => db.Assets.Where(a => a.Id == assetId).Select(a => a.PublicId).SingleAsync());
        api.Photos.Stored[publicId] = new StoredPhoto(2 * 1024 * 1024, "jpg");

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/essays/batch")
        {
            Content = JsonContent.Create(new { essays = new[] { new { promptId, assetId } }, aiNoticeAccepted = true }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await student.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var essayId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("essayIds")[0].GetGuid();

        await RunAsync(JobTypes.Transcribe);
        api.Grader.Script.Enqueue(() => correction.ToJsonString());
        await RunAsync(JobTypes.Grade);

        Assert.Equal(EssayStatus.Completed, await api.QueryAsync(db => db.Essays.Where(e => e.Id == essayId).Select(e => e.Status).SingleAsync()));
        return essayId;
    }

    private async Task RunAsync(params string[] types)
    {
        var runner = api.Factory.Services.GetRequiredService<IJobRunner>();
        while (await runner.RunNextAsync(types, CancellationToken.None))
        {
        }
    }
}
