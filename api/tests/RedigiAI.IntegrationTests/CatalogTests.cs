using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RedigiAI.Application.Catalog;

namespace RedigiAI.IntegrationTests;

[Collection(ApiCollection.Name)]
public class CatalogTests(ApiFixture api)
{
    // O catálogo é só para quem está logado.
    private readonly HttpClient _client = api.RegisteredClient();

    [Fact]
    public async Task Exams_list_fuvest_as_active_and_the_others_as_coming_soon()
    {
        var exams = (await _client.GetFromJsonAsync<JsonElement>("/v1/exams")).EnumerateArray().ToList();

        Assert.Equal(["fuvest", "unicamp", "enem"], exams.Select(e => e.GetProperty("slug").GetString()));
        Assert.Equal(["active", "coming_soon", "coming_soon"], exams.Select(e => e.GetProperty("status").GetString()));
        Assert.Equal(30, exams[0].GetProperty("sheetLines").GetInt32());
        Assert.Equal(JsonValueKind.String, exams[0].GetProperty("currentRubricVersionId").ValueKind);
        Assert.Equal(JsonValueKind.Null, exams[1].GetProperty("currentRubricVersionId").ValueKind);
    }

    [Fact]
    public async Task Prompts_list_brings_title_type_and_number_of_texts()
    {
        var prompts = await _client.GetFromJsonAsync<JsonElement>("/v1/exams/fuvest/prompts");

        var prompt = prompts.EnumerateArray().Single(p => p.GetProperty("slug").GetString() == "algoritmos-e-o-debate-publico");
        Assert.Equal("Algoritmos e o debate público", prompt.GetProperty("title").GetString());
        Assert.Equal("Fuvest", prompt.GetProperty("examName").GetString());
        Assert.Equal(4, prompt.GetProperty("textCount").GetInt32());
        var essayType = prompt.GetProperty("essayType");
        Assert.Equal(("dissertacao-em-prosa", "Dissertação em prosa"), (essayType.GetProperty("slug").GetString(), essayType.GetProperty("name").GetString()));
        // O aluno não escolhe tipo: a lista não oferece tipos. Linhas e título ficam nos itens das Instruções.
        Assert.False(prompt.TryGetProperty("essayTypes", out _));
        Assert.False(prompt.TryGetProperty("genreLabel", out _));
        Assert.False(prompt.TryGetProperty("minLines", out _));
    }

    [Theory]
    [InlineData("algoritmos", 1)]
    [InlineData("DEBATE", 1)]
    [InlineData("clima", 0)]
    [InlineData("%", 0)]
    [InlineData("_lgoritmos", 0)]
    [InlineData("algo\u0000ritmos", 1)] // caractere de controle é descartado, não derruba a consulta
    public async Task Prompt_search_matches_the_title_literally(string search, int expected)
    {
        var prompts = await _client.GetFromJsonAsync<JsonElement>($"/v1/exams/fuvest/prompts?q={Uri.EscapeDataString(search)}");

        Assert.Equal(expected, prompts.EnumerateArray().Count(p => p.GetProperty("slug").GetString() == "algoritmos-e-o-debate-publico"));
    }

    [Theory]
    [InlineData("unicamp")]
    [InlineData("inexistente")]
    public async Task Prompts_of_an_unavailable_exam_are_not_found(string slug)
    {
        var response = await _client.GetAsync($"/v1/exams/{slug}/prompts");

        await AccountTests.AssertErrorAsync(response, HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Prompt_detail_brings_the_blocks_in_order_and_the_type()
    {
        var prompt = await _client.GetFromJsonAsync<JsonElement>("/v1/prompts/algoritmos-e-o-debate-publico");

        Assert.Equal(("fuvest", 30, 1, "published"), (
            prompt.GetProperty("examSlug").GetString(), prompt.GetProperty("sheetLines").GetInt32(),
            prompt.GetProperty("version").GetInt32(), prompt.GetProperty("status").GetString()));
        Assert.Equal(("Dissertação em prosa", "Fuvest – Dissertação em prosa – Algoritmos e o debate público"), (
            prompt.GetProperty("essayType").GetProperty("name").GetString(), prompt.GetProperty("essayTitle").GetString()));
        Assert.Equal(4, prompt.GetProperty("textCount").GetInt32());
        Assert.False(prompt.TryGetProperty("titleRequired", out _));
        var blocks = prompt.GetProperty("blocks").EnumerateArray().ToList();
        Assert.Equal([10, 20, 30, 40, 50, 60], blocks.Select(b => b.GetProperty("position").GetInt32()));
        Assert.Equal(
            ["collection_item", "collection_item", "collection_item", "collection_item", "command", "instructions"],
            blocks.Select(b => b.GetProperty("kind").GetString()));
        Assert.Equal(["prose", "prose", "verse", "prose"], blocks.Take(4).Select(b => b.GetProperty("data").GetProperty("format").GetString()));
        Assert.Equal(2, blocks[2].GetProperty("data").GetProperty("columns").GetArrayLength());
        Assert.False(string.IsNullOrEmpty(blocks[4].GetProperty("data").GetProperty("body").GetString()));
        // As abordagens esperadas são só para a IA.
        Assert.False(prompt.TryGetProperty("gradingNotes", out _));
    }

    [Theory]
    [InlineData("/v1/prompts/nao-existe")]
    [InlineData("/v1/prompts/algoritmos-e-o-debate-publico?version=2")]
    public async Task Unknown_prompt_is_not_available(string path)
    {
        var response = await _client.GetAsync(path);

        var error = await AccountTests.AssertErrorAsync(response, HttpStatusCode.NotFound, "PROMPT_NOT_AVAILABLE");
        Assert.Equal("Essa proposta não está disponível. Escolha outra.", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Rubric_brings_the_four_criteria_with_weights_and_zero_rules()
    {
        var exams = await _client.GetFromJsonAsync<JsonElement>("/v1/exams");
        var rubricId = exams[0].GetProperty("currentRubricVersionId").GetGuid();

        var rubric = await _client.GetFromJsonAsync<JsonElement>($"/v1/rubrics/{rubricId}");

        Assert.Equal(1, rubric.GetProperty("version").GetInt32());
        Assert.Equal((4, 40), (rubric.GetProperty("maxLevel").GetInt32(), rubric.GetProperty("maxScore").GetInt32()));
        var criteria = rubric.GetProperty("criteria").EnumerateArray().ToList();
        Assert.Equal([3, 2, 3, 2], criteria.Select(c => c.GetProperty("weight").GetInt32()));
        Assert.Equal([12, 8, 12, 8], criteria.Select(c => c.GetProperty("maxPoints").GetInt32()));
        Assert.Equal("Correção gramatical, convenção da escrita e adequação vocabular", criteria[3].GetProperty("name").GetString());
        Assert.StartsWith(
            "Ótima articulação formal e de sentido",
            criteria[2].GetProperty("levels")[3].GetProperty("descriptor").GetString());
        Assert.All(criteria, c => Assert.Equal(
            [1, 2, 3, 4], c.GetProperty("levels").EnumerateArray().Select(l => l.GetProperty("level").GetInt32())));
        Assert.Equal(
            ["theme_not_addressed", "modality_not_met", "collection_copy", "candidate_identification"],
            rubric.GetProperty("zeroRules").EnumerateArray().Select(z => z.GetProperty("code").GetString()));
    }

    [Theory]
    [InlineData("/v1/rubrics/0198f0c0-0000-7000-8000-000000000000")]
    [InlineData("/v1/themes/not-a-guid")]
    [InlineData("/v1/exams/fuvest/themes")]
    [InlineData("/v1/unknown")]
    public async Task Unknown_resources_use_the_error_format(string path)
    {
        var response = await _client.GetAsync(path);

        await AccountTests.AssertErrorAsync(response, HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Seeding_twice_changes_nothing()
    {
        await using var scope = api.Factory.Services.CreateAsyncScope();

        var seeded = await scope.ServiceProvider.GetRequiredService<ICatalogSeedService>().SeedAsync(CancellationToken.None);

        Assert.False(seeded);
        Assert.Equal(3, (await _client.GetFromJsonAsync<JsonElement>("/v1/exams")).GetArrayLength());
    }
}
