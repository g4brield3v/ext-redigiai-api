using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RedigiAI.Infrastructure.Persistence;

namespace RedigiAI.IntegrationTests;

[Collection(ApiCollection.Name)]
public class CatalogTests(ApiFixture api)
{
    private readonly HttpClient _client = api.CreateClient();

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
    public async Task Themes_list_accepted_types_and_number_of_texts()
    {
        var themes = await _client.GetFromJsonAsync<JsonElement>("/v1/exams/fuvest/themes");

        var theme = Assert.Single(themes.EnumerateArray());
        Assert.Equal("Algoritmos e o debate público", theme.GetProperty("title").GetString());
        Assert.Equal("Fuvest", theme.GetProperty("examName").GetString());
        Assert.Equal(4, theme.GetProperty("textCount").GetInt32());
        Assert.Equal(
            ["Dissertação", "Narrativa"],
            theme.GetProperty("essayTypes").EnumerateArray().Select(t => t.GetProperty("name").GetString()));
    }

    [Theory]
    [InlineData("algoritmos", 1)]
    [InlineData("DEBATE", 1)]
    [InlineData("clima", 0)]
    [InlineData("%", 0)]
    [InlineData("_lgoritmos", 0)]
    public async Task Theme_search_matches_the_title_literally(string search, int expected)
    {
        var themes = await _client.GetFromJsonAsync<JsonElement>($"/v1/exams/fuvest/themes?q={Uri.EscapeDataString(search)}");

        Assert.Equal(expected, themes.GetArrayLength());
    }

    [Theory]
    [InlineData("unicamp")]
    [InlineData("inexistente")]
    public async Task Themes_of_an_unavailable_exam_are_not_found(string slug)
    {
        var response = await _client.GetAsync($"/v1/exams/{slug}/themes");

        await AccountTests.AssertErrorAsync(response, HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Theme_detail_brings_statement_texts_and_types()
    {
        var themeId = (await _client.GetFromJsonAsync<JsonElement>("/v1/exams/fuvest/themes"))[0].GetProperty("id").GetGuid();

        var theme = await _client.GetFromJsonAsync<JsonElement>($"/v1/themes/{themeId}");

        Assert.Equal("fuvest", theme.GetProperty("examSlug").GetString());
        Assert.Equal(30, theme.GetProperty("sheetLines").GetInt32());
        Assert.False(string.IsNullOrEmpty(theme.GetProperty("statement").GetString()));
        var texts = theme.GetProperty("texts").EnumerateArray().ToList();
        Assert.Equal([1, 2, 3, 4], texts.Select(t => t.GetProperty("position").GetInt32()));
        Assert.Equal(["text", "text", "image", "text"], texts.Select(t => t.GetProperty("kind").GetString()));
        Assert.Equal(
            ["dissertacao", "narrativa"],
            theme.GetProperty("essayTypes").EnumerateArray().Select(t => t.GetProperty("slug").GetString()));
    }

    [Fact]
    public async Task Unknown_theme_is_not_available()
    {
        var response = await _client.GetAsync($"/v1/themes/{Guid.CreateVersion7()}");

        var error = await AccountTests.AssertErrorAsync(response, HttpStatusCode.NotFound, "THEME_NOT_AVAILABLE");
        Assert.Equal("Esse tema não está disponível. Escolha outro.", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Rubric_brings_the_four_criteria_with_weights_and_zero_rules()
    {
        var exams = await _client.GetFromJsonAsync<JsonElement>("/v1/exams");
        var rubricId = exams[0].GetProperty("currentRubricVersionId").GetGuid();

        var rubric = await _client.GetFromJsonAsync<JsonElement>($"/v1/rubrics/{rubricId}");

        Assert.Equal(1, rubric.GetProperty("version").GetInt32());
        Assert.Equal(30, rubric.GetProperty("maxScore").GetInt32());
        var criteria = rubric.GetProperty("criteria").EnumerateArray().ToList();
        Assert.Equal([3, 2, 3, 2], criteria.Select(c => c.GetProperty("weight").GetInt32()));
        Assert.Equal([9, 6, 9, 6], criteria.Select(c => c.GetProperty("maxPoints").GetInt32()));
        Assert.All(criteria, c => Assert.Equal(
            [1, 2, 3], c.GetProperty("levels").EnumerateArray().Select(l => l.GetProperty("level").GetInt32())));
        Assert.Equal(
            ["theme_not_developed", "type_not_met", "full_copy", "candidate_identified"],
            rubric.GetProperty("zeroRules").EnumerateArray().Select(z => z.GetProperty("code").GetString()));
    }

    [Theory]
    [InlineData("/v1/rubrics/0198f0c0-0000-7000-8000-000000000000")]
    [InlineData("/v1/themes/not-a-guid")]
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

        var seeded = await scope.ServiceProvider.GetRequiredService<CatalogSeeder>().SeedAsync(CancellationToken.None);

        Assert.False(seeded);
        Assert.Equal(3, (await _client.GetFromJsonAsync<JsonElement>("/v1/exams")).GetArrayLength());
    }
}
