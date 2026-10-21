using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RedigiAI.Api;
using RedigiAI.Application.Catalog;
using RedigiAI.Application.Grading;
using RedigiAI.Domain.Catalog;
using RedigiAI.Domain.Essays;

namespace RedigiAI.IntegrationTests;

/// <summary>Comando <c>import-prompts</c>: publica as propostas que vêm com o programa (decisão de 21/10/2026).</summary>
[Collection(ApiCollection.Name)]
public class PromptImportTests(ApiFixture api)
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52];

    private static string Document(string slug) => $$"""
        {
          "exam": "fuvest",
          "slug": "{{slug}}",
          "title": "O papel da memória na vida pública",
          "summary": "Inspirada em proposta da Fuvest.",
          "sourceLabel": "Textos próprios",
          "essayType": "dissertacao-em-prosa",
          "blocks": [
            { "position": 10, "kind": "collection_item", "data": {
                "label": "Texto 1", "format": "prose", "hideLabel": true,
                "body": "Lembrar é escolher o que fica.", "source": "Texto elaborado pela equipe RedigiAI.",
                "notes": ["* Memória: aqui, a lembrança coletiva."] } },
            { "position": 20, "kind": "collection_item", "data": {
                "label": "Texto 2", "format": "image", "width": "half", "imageFile": "grafico.png",
                "alt": "Gráfico de barras com visitas a museus.",
                "aiDescription": "Gráfico de barras: as visitas a museus caem de 100 para 60 em dez anos, segundo o texto.",
                "source": "Elaborado pela equipe RedigiAI." } },
            { "position": 30, "kind": "collection_item", "data": {
                "label": "Texto 3", "format": "verse", "width": "half",
                "columns": [["Quem guarda a rua", "guarda o nome"]], "source": "Texto elaborado pela equipe RedigiAI." } },
            { "position": 40, "kind": "command", "data": { "body": "Redija uma dissertação em prosa sobre **o papel da memória**." } },
            { "position": 50, "kind": "instructions", "data": { "items": ["Dê um título a sua redação."] } }
          ],
          "gradingNotes": { "expectedFocus": "A memória coletiva e o presente.", "tangentSignals": "Fala só de memória individual." }
        }
        """;

    private static PromptPackage Package(string slug, string name = "teste", bool withImage = true) =>
        new(name, Document(slug), withImage ? new Dictionary<string, byte[]> { ["grafico.png"] = Png } : new Dictionary<string, byte[]>());

    private async Task<string> AdminAsync()
    {
        var userId = ApiFixture.NewClerkUserId();
        var client = api.CreateClient(api.CreateToken(userId, email: ApiFixture.AdminEmail));
        (await client.PostAsJsonAsync("/v1/me", new { birthDate = "2000-05-17", termsAccepted = true })).EnsureSuccessStatusCode();
        await api.MakeAdminAsync(userId);
        return userId;
    }

    private async Task<IReadOnlyList<PromptImportResult>> ImportAsync(string admin, params PromptPackage[] packages)
    {
        await using var scope = api.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPromptImportService>().ImportAsync(admin, packages, CancellationToken.None);
    }

    private static string Slug() => $"memoria-{Guid.NewGuid():N}"[..20];

    [Fact]
    public async Task Imports_and_publishes_with_the_image_side_by_side_texts_and_notes()
    {
        var admin = await AdminAsync();
        var slug = Slug();

        var results = await ImportAsync(admin, Package(slug));

        Assert.Equal(PromptImportOutcome.Created, Assert.Single(results).Outcome);
        var prompt = await api.QueryAsync(db => db.Prompts.Include(p => p.Blocks.OrderBy(b => b.Position)).SingleAsync(p => p.Slug == slug));
        Assert.Equal(PromptStatus.Published, prompt.Status);
        Assert.NotNull(prompt.CreatedBy);
        Assert.Equal(prompt.CreatedBy, prompt.PublishedBy);

        // A imagem foi enviada com um identificador que sai do conteúdo, e registrada como imagem de proposta.
        var image = prompt.Blocks[1].Data;
        var asset = await api.QueryAsync(db => db.Assets.SingleAsync(a => a.Id == image.AssetId));
        Assert.Equal(AssetScope.PromptImage, asset.Scope);
        Assert.StartsWith($"prompts/import/{slug}/", asset.PublicId);
        Assert.True(api.PromptImages.Uploaded.ContainsKey(asset.PublicId));

        // O aluno recebe a largura, o rótulo escondido e as notas.
        var student = api.CreateClient(api.CreateToken(ApiFixture.NewClerkUserId()));
        (await student.PostAsJsonAsync("/v1/me", new { birthDate = "2000-05-17", termsAccepted = true })).EnsureSuccessStatusCode();
        var page = JsonNode.Parse(await student.GetStringAsync($"/v1/prompts/{slug}"))!;
        var blocks = page["blocks"]!.AsArray();
        Assert.True(blocks[0]!["data"]!["hideLabel"]!.GetValue<bool>());
        Assert.Equal("* Memória: aqui, a lembrança coletiva.", blocks[0]!["data"]!["notes"]![0]!.GetValue<string>());
        Assert.Null(blocks[0]!["data"]!["width"]);
        Assert.Equal("half", blocks[1]!["data"]!["width"]!.GetValue<string>());
        Assert.Equal("half", blocks[2]!["data"]!["width"]!.GetValue<string>());
        Assert.NotNull(blocks[1]!["data"]!["imageUrl"]);

        // A correção recebe o rótulo (mesmo escondido na tela) e as notas.
        var text = PromptText.Blocks(prompt, "Dissertação em prosa");
        Assert.Contains("[Texto 1] Lembrar é escolher o que fica. (Notas: * Memória: aqui, a lembrança coletiva.) — Texto elaborado", text);
    }

    [Fact]
    public async Task Running_again_changes_nothing_and_sends_no_image()
    {
        var admin = await AdminAsync();
        var slug = Slug();
        await ImportAsync(admin, Package(slug));
        var uploads = api.PromptImages.Uploaded.Keys.Count(key => key.Contains(slug));
        var assets = await api.QueryAsync(db => db.Assets.CountAsync(a => a.PublicId.Contains(slug)));

        var again = await ImportAsync(admin, Package(slug));

        Assert.Equal(PromptImportOutcome.AlreadyPresent, Assert.Single(again).Outcome);
        Assert.Equal(1, await api.QueryAsync(db => db.Prompts.CountAsync(p => p.Slug == slug)));
        Assert.Equal(1, uploads);
        Assert.Equal(assets, await api.QueryAsync(db => db.Assets.CountAsync(a => a.PublicId.Contains(slug))));
    }

    [Fact]
    public async Task Image_already_sent_by_a_failed_run_is_reused()
    {
        var admin = await AdminAsync();
        var slug = Slug();
        var broken = new PromptPackage("quebrado", Document(slug).Replace("\"essayType\": \"dissertacao-em-prosa\"", "\"essayType\": \"inexistente\""),
            new Dictionary<string, byte[]> { ["grafico.png"] = Png });

        Assert.Equal(PromptImportOutcome.Failed, Assert.Single(await ImportAsync(admin, broken)).Outcome);
        Assert.Equal(PromptImportOutcome.Created, Assert.Single(await ImportAsync(admin, Package(slug))).Outcome);

        Assert.Equal(1, await api.QueryAsync(db => db.Assets.CountAsync(a => a.PublicId.Contains(slug))));
    }

    [Fact]
    public async Task A_package_that_fails_does_not_stop_the_others()
    {
        var admin = await AdminAsync();
        var (missing, fine) = (Slug(), Slug());

        var results = await ImportAsync(admin, Package(missing, "a-sem-imagem", withImage: false), Package(fine, "b-certo"));

        var failed = results.Single(result => result.Package == "a-sem-imagem");
        Assert.Equal(PromptImportOutcome.Failed, failed.Outcome);
        Assert.Contains("grafico.png", Assert.Single(failed.Problems));
        Assert.Equal(PromptImportOutcome.Created, results.Single(result => result.Package == "b-certo").Outcome);
        Assert.False(await api.QueryAsync(db => db.Prompts.AnyAsync(p => p.Slug == missing)));
    }

    [Fact]
    public async Task Only_an_administrator_imports()
    {
        var student = ApiFixture.NewClerkUserId();
        var client = api.CreateClient(api.CreateToken(student));
        (await client.PostAsJsonAsync("/v1/me", new { birthDate = "2000-05-17", termsAccepted = true })).EnsureSuccessStatusCode();
        var slug = Slug();

        await Assert.ThrowsAsync<InvalidOperationException>(() => ImportAsync(student, Package(slug)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ImportAsync("user_inexistente", Package(slug)));

        Assert.False(await api.QueryAsync(db => db.Prompts.AnyAsync(p => p.Slug == slug)));
    }

    /// <summary>As propostas que vêm com o programa publicam sem pendência nenhuma.</summary>
    [Fact]
    public async Task Bundled_prompts_are_all_published()
    {
        var packages = BundledPrompts.Packages();
        Assert.NotEmpty(packages);

        var results = await ImportAsync(await AdminAsync(), [.. packages]);

        Assert.All(results, result => Assert.True(
            result.Outcome != PromptImportOutcome.Failed, $"{result.Package}: {string.Join(" | ", result.Problems)}"));
        Assert.Equal(packages.Count, results.Count);
    }
}
