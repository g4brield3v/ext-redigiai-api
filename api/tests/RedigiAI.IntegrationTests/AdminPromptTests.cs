using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using RedigiAI.Domain.Grading;
using Microsoft.EntityFrameworkCore;
using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Catalog;
using RedigiAI.Domain.Essays;

namespace RedigiAI.IntegrationTests;

/// <summary>Área de administração das propostas (docs/07-propostas-de-redacao.md, A5 e A8).</summary>
[Collection(ApiCollection.Name)]
public class AdminPromptTests(ApiFixture api)
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52];
    private static readonly Guid AnyId = Guid.CreateVersion7();

    public static TheoryData<string, string> AdminRoutes => new()
    {
        { "POST", "/v1/admin/assets" },
        { "GET", "/v1/admin/exams/fuvest/essay-types" },
        { "POST", "/v1/admin/prompts" },
        { "GET", "/v1/admin/prompts" },
        { "GET", $"/v1/admin/prompts/{AnyId}" },
        { "PUT", $"/v1/admin/prompts/{AnyId}" },
        { "PATCH", $"/v1/admin/prompts/{AnyId}/order" },
        { "POST", $"/v1/admin/prompts/{AnyId}/validate" },
        { "POST", $"/v1/admin/prompts/{AnyId}/publish" },
        { "POST", $"/v1/admin/prompts/{AnyId}/new-version" },
        { "POST", $"/v1/admin/prompts/{AnyId}/archive" },
        { "DELETE", $"/v1/admin/prompts/{AnyId}" },
    };

    [Theory]
    [MemberData(nameof(AdminRoutes))]
    public async Task Admin_route_without_token_is_refused(string method, string path)
    {
        var response = await api.CreateClient().SendAsync(Request(method, path));

        await AccountTests.AssertErrorAsync(response, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Theory]
    [MemberData(nameof(AdminRoutes))]
    public async Task Admin_route_does_not_exist_for_a_student(string method, string path)
    {
        var student = await StudentAsync();

        // Mesmo com corpo válido ou inválido: a resposta é a de rota inexistente, antes de qualquer leitura.
        await AccountTests.AssertErrorAsync(await student.SendAsync(Request(method, path)), HttpStatusCode.NotFound, "NOT_FOUND");
        await AccountTests.AssertErrorAsync(await student.SendAsync(Request(method, path, "{ not json")), HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public void Every_admin_route_is_guarded_by_the_admin_check()
    {
        // Uma rota nova em /v1/admin sem a conferência ficaria aberta a qualquer aluno logado.
        var routes = api.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText!.TrimStart('/').StartsWith("v1/admin", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Equal(12, routes.Count);
        Assert.All(routes, route => Assert.Contains(route.Metadata, item => item.GetType().Name == "AdminOnlyAttribute"));
        Assert.All(routes, route => Assert.NotEmpty(route.Metadata.OfType<Microsoft.AspNetCore.Authorization.IAuthorizeData>()));
    }

    [Fact]
    public async Task Admin_routes_have_their_own_request_limit()
    {
        var userId = await RegisterAdminAsync();
        await using var factory = api.CreateFactory(builder => builder.UseSetting("RateLimit:AdminPerUserPerMinute", "3"));
        var admin = ApiFixture.CreateClient(factory, api.CreateToken(userId, email: ApiFixture.AdminEmail));

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            statuses.Add((await admin.GetAsync("/v1/admin/prompts")).StatusCode);
        }

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], statuses);
        // O limite da administração não gasta o das outras rotas.
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Essay_types_come_from_the_database_with_their_profile_status()
    {
        var admin = await AdminAsync();
        var narrativa = await api.QueryAsync(db => db.EssayTypes.Where(t => t.Slug == "narrativa").Select(t => t.Id).SingleAsync());
        var profile = await api.QueryAsync(db => db.GradingProfiles
            .Where(p => p.EssayTypeId == narrativa && p.Status == GradingProfileStatus.Active).Select(p => p.Id).SingleAsync());

        var types = (await admin.GetFromJsonAsync<JsonElement>("/v1/admin/exams/fuvest/essay-types")).EnumerateArray().ToList();

        // A primeira é a dissertação em prosa; não existe mais a categoria "dissertacao".
        Assert.Equal(
            [("dissertacao-em-prosa", "Dissertação em prosa", true), ("narrativa", "Narrativa", true)],
            types.Select(t => (t.GetProperty("slug").GetString(), t.GetProperty("name").GetString(), t.GetProperty("hasActiveProfile").GetBoolean())));
        try
        {
            await api.QueryAsync(db => db.GradingProfiles.Where(p => p.Id == profile)
                .ExecuteUpdateAsync(p => p.SetProperty(x => x.Status, GradingProfileStatus.Retired)));

            var withoutProfile = (await admin.GetFromJsonAsync<JsonElement>("/v1/admin/exams/fuvest/essay-types")).EnumerateArray()
                .Single(t => t.GetProperty("slug").GetString() == "narrativa");
            Assert.False(withoutProfile.GetProperty("hasActiveProfile").GetBoolean());
        }
        finally
        {
            await api.QueryAsync(db => db.GradingProfiles.Where(p => p.Id == profile)
                .ExecuteUpdateAsync(p => p.SetProperty(x => x.Status, GradingProfileStatus.Active)));
        }

        await AccountTests.AssertErrorAsync(await admin.GetAsync("/v1/admin/exams/inexistente/essay-types"), HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Card_with_three_items_is_saved_exported_and_validated()
    {
        var admin = await AdminAsync();
        var image = (await UploadAsync(admin, Png, "grafico.png")).GetProperty("assetId").GetGuid();
        var document = ScienceDocument(NewSlug(), image);
        JsonObject Card(params JsonObject[] items) => new()
        {
            ["position"] = 65,
            ["kind"] = "card",
            ["data"] = new JsonObject { ["title"] = "Para refletir", ["cardItems"] = new JsonArray([.. items]) },
        };
        JsonObject Text(string body) => new() { ["format"] = "text", ["body"] = body, ["caption"] = "Fonte: IBGE, 2024." };
        var picture = new JsonObject
        {
            ["format"] = "image",
            ["assetId"] = image.ToString(),
            ["alt"] = "Gráfico de barras.",
            ["aiDescription"] = "Gráfico de barras com o investimento em pesquisa de 2010 a 2024, em queda.",
        };

        document["blocks"]!.AsArray().Add(Card(Text("Primeiro."), picture.DeepClone().AsObject(), Text("Terceiro.")));
        var id = await CreateAsync(admin, document);

        var exported = await admin.GetFromJsonAsync<JsonElement>($"/v1/admin/prompts/{id}");
        var card = exported.GetProperty("blocks").EnumerateArray().Single(b => b.GetProperty("kind").GetString() == "card");
        var items = card.GetProperty("data").GetProperty("cardItems").EnumerateArray().ToList();
        Assert.Equal(["text", "image", "text"], items.Select(i => i.GetProperty("format").GetString()));
        Assert.Equal("Fonte: IBGE, 2024.", items[0].GetProperty("caption").GetString());
        Assert.False(string.IsNullOrEmpty(items[1].GetProperty("imageUrl").GetString()));
        Assert.Empty(exported.GetProperty("issues").EnumerateArray());

        // Com 2 ou 4 itens, nem o rascunho é guardado.
        foreach (var count in new[] { 2, 4 })
        {
            var wrong = ScienceDocument(NewSlug(), image);
            wrong["blocks"]!.AsArray().Add(Card([.. Enumerable.Range(1, count).Select(i => Text($"Item {i}."))]));
            var refused = await admin.SendAsync(Create(wrong, Guid.NewGuid().ToString()));
            Assert.Equal(["blocks[6].data.cardItems"], Paths(await AssertValidationFailedAsync(refused)));
        }

        // Imagem sem descrição para a IA vira pendência.
        var noDescription = ScienceDocument(NewSlug(), image);
        var bare = picture.DeepClone().AsObject();
        bare.Remove("aiDescription");
        noDescription["blocks"]!.AsArray().Add(Card(Text("Primeiro."), bare, Text("Terceiro.")));
        var pending = await (await admin.SendAsync(Create(noDescription, Guid.NewGuid().ToString()))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["blocks[6].data.cardItems[1].aiDescription"], Paths(pending.GetProperty("issues")));
    }

    [Fact]
    public async Task Category_without_an_active_grading_profile_blocks_publication()
    {
        var admin = await AdminAsync();
        var document = ScienceDocument(NewSlug(), (await UploadAsync(admin, Png, "tirinha.png")).GetProperty("assetId").GetGuid());
        document["essayType"] = "narrativa";
        var narrativa = await api.QueryAsync(db => db.EssayTypes.Where(t => t.Slug == "narrativa").Select(t => t.Id).SingleAsync());
        var profile = await api.QueryAsync(db => db.GradingProfiles
            .Where(p => p.EssayTypeId == narrativa && p.Status == GradingProfileStatus.Active).Select(p => p.Id).SingleAsync());

        // Com perfil ativo, a narrativa publica; sem ele, vira pendência.
        var withProfile = await admin.SendAsync(Create(document.DeepClone(), Guid.NewGuid().ToString()));
        Assert.Empty((await withProfile.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("issues").EnumerateArray());
        try
        {
            await api.QueryAsync(db => db.GradingProfiles.Where(p => p.Id == profile)
                .ExecuteUpdateAsync(p => p.SetProperty(x => x.Status, GradingProfileStatus.Retired)));
            document["slug"] = NewSlug();

            var id = await CreateAsync(admin, document);

            var validation = await (await admin.PostAsync($"/v1/admin/prompts/{id}/validate", null)).Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(validation.GetProperty("valid").GetBoolean());
            Assert.Equal(["essayType"], Paths(validation.GetProperty("issues")));
            Assert.Equal(["essayType"], Paths(await AssertValidationFailedAsync(await admin.PostAsync($"/v1/admin/prompts/{id}/publish", null))));
        }
        finally
        {
            await api.QueryAsync(db => db.GradingProfiles.Where(p => p.Id == profile)
                .ExecuteUpdateAsync(p => p.SetProperty(x => x.Status, GradingProfileStatus.Active)));
        }
    }

    [Fact]
    public async Task Reordering_and_uploading_go_to_the_audit_log_too()
    {
        var admin = await AdminAsync();
        var image = (await UploadAsync(admin, Png, "tirinha.png")).GetProperty("assetId").GetGuid();
        var id = await CreateAsync(admin, ScienceDocument(NewSlug(), image));
        var blocks = (await admin.GetFromJsonAsync<JsonElement>($"/v1/admin/prompts/{id}")).GetProperty("blocks").EnumerateArray()
            .Select((block, index) => new { id = block.GetProperty("id").GetGuid(), position = (7 - index) * 10 }).ToArray();

        (await admin.PatchAsJsonAsync($"/v1/admin/prompts/{id}/order", new { blocks })).EnsureSuccessStatusCode();

        var entries = await api.QueryAsync(db => db.AuditLog
            .Where(a => a.EntityId == id || a.EntityId == image).Select(a => new { a.Action, a.Entity, a.Actor }).ToListAsync());
        Assert.Equal(["asset.uploaded", "prompt.created", "prompt.reordered"], entries.Select(e => e.Action).Order());
        Assert.Single(entries.Select(e => e.Actor).Distinct());
        Assert.StartsWith("user:", entries[0].Actor);
    }

    [Fact]
    public async Task Empty_draft_is_created_once_per_idempotency_key()
    {
        var admin = await AdminAsync();
        var key = Guid.NewGuid().ToString();

        var first = await admin.SendAsync(Create(null, key));
        var second = await admin.SendAsync(Create(null, key));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var draft = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(draft.GetProperty("id").GetGuid(), (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        Assert.Equal(("draft", 1), (draft.GetProperty("status").GetString(), draft.GetProperty("version").GetInt32()));
        Assert.StartsWith("proposta", draft.GetProperty("slug").GetString());
        Assert.Contains("title", Paths(draft.GetProperty("issues")));
        Assert.Contains("gradingNotes.expectedFocus", Paths(draft.GetProperty("issues")));

        await AccountTests.AssertErrorAsync(await admin.SendAsync(Create(null, null)), HttpStatusCode.UnprocessableEntity, "VALIDATION_ERROR");
    }

    [Fact]
    public async Task Slug_comes_from_the_title_when_it_is_not_given()
    {
        var admin = await AdminAsync();
        var mark = Guid.NewGuid().ToString("N")[..8];
        var document = new JsonObject { ["title"] = $"  A Ciência & a Arte: irmãs? {mark}  " };

        var first = await admin.SendAsync(Create(document, Guid.NewGuid().ToString()));
        var second = await admin.SendAsync(Create(document, Guid.NewGuid().ToString()));

        var slug = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("slug").GetString();
        Assert.Equal($"a-ciencia-a-arte-irmas-{mark}", slug);
        // O mesmo título de novo ganha um sufixo, em vez de falhar.
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Matches($"^{slug}-[0-9a-f]{{6}}$", (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("slug").GetString());
    }

    [Fact]
    public async Task Science_prompt_file_is_imported_and_published()
    {
        var admin = await AdminAsync();
        var image = await UploadAsync(admin, Png, "tirinha.png");
        var slug = NewSlug();
        var document = ScienceDocument(slug, image.GetProperty("assetId").GetGuid());

        var created = await admin.SendAsync(Create(document, Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var draft = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(draft.GetProperty("issues").EnumerateArray());
        Assert.Equal([10, 20, 30, 40, 50, 60, 70], draft.GetProperty("blocks").EnumerateArray().Select(block => block.GetProperty("position").GetInt32()));
        var id = draft.GetProperty("id").GetGuid();

        var validation = await (await admin.PostAsync($"/v1/admin/prompts/{id}/validate", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(validation.GetProperty("valid").GetBoolean());

        var published = await admin.PostAsync($"/v1/admin/prompts/{id}/publish", null);
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        Assert.Equal("published", (await published.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        var exported = await admin.GetFromJsonAsync<JsonElement>($"/v1/admin/prompts/{id}");
        Assert.Equal(("fuvest", "dissertacao-em-prosa"), (exported.GetProperty("exam").GetString(), exported.GetProperty("essayType").GetString()));
        // O tipo é só a categoria: não há gênero em texto livre nem limites de linha no cadastro.
        Assert.False(exported.TryGetProperty("genreLabel", out _));
        Assert.False(exported.TryGetProperty("sheet", out _));
        var strip = exported.GetProperty("blocks")[0];
        Assert.Equal(("collection_item", "image"), (strip.GetProperty("kind").GetString(), strip.GetProperty("data").GetProperty("format").GetString()));
        Assert.Equal(image.GetProperty("url").GetString(), strip.GetProperty("data").GetProperty("imageUrl").GetString());
        // Cada bloco exporta só os campos do seu tipo.
        Assert.False(strip.GetProperty("data").TryGetProperty("body", out _));
        Assert.Equal(3, exported.GetProperty("blocks")[2].GetProperty("data").GetProperty("columns").GetArrayLength());
        Assert.Equal(4, exported.GetProperty("gradingNotes").GetProperty("keyData").GetArrayLength());

        var saved = await api.QueryAsync(db => db.Prompts.SingleAsync(p => p.Id == id));
        Assert.NotNull(saved.PublishedBy);
        Assert.Equal(saved.CreatedBy, saved.PublishedBy);
    }

    [Fact]
    public async Task Exported_prompt_is_imported_again_as_it_is()
    {
        var admin = await AdminAsync();
        var image = await UploadAsync(admin, Png, "tirinha.png");
        var original = await CreateAsync(admin, ScienceDocument(NewSlug(), image.GetProperty("assetId").GetGuid()));
        var exported = JsonNode.Parse((await admin.GetStringAsync($"/v1/admin/prompts/{original}"))!)!.AsObject();
        exported["slug"] = NewSlug();

        var copy = await CreateAsync(admin, exported);

        var again = JsonNode.Parse(await admin.GetStringAsync($"/v1/admin/prompts/{copy}"))!.AsObject();
        Assert.Equal(Comparable(exported), Comparable(again));
        Assert.Empty(again["issues"]!.AsArray());
    }

    [Fact]
    public async Task Image_is_recognised_by_its_content()
    {
        var admin = await AdminAsync();

        var uploaded = await UploadAsync(admin, Png, "qualquer-nome.jpg", "image/jpeg");

        var assetId = uploaded.GetProperty("assetId").GetGuid();
        Assert.Equal((800, 240), (uploaded.GetProperty("width").GetInt32(), uploaded.GetProperty("height").GetInt32()));
        var asset = await api.QueryAsync(db => db.Assets.SingleAsync(a => a.Id == assetId));
        Assert.Equal((AssetScope.PromptImage, "image/png", $"prompts/{assetId:N}"), (asset.Scope, asset.Mime, asset.PublicId));
        Assert.Equal("image/png", api.PromptImages.Uploaded[asset.PublicId].ContentType);
        // Privada: só um endereço assinado de download, nunca a entrega pública.
        Assert.Equal($"https://api.cloudinary.test/image/download?public_id=prompts/{assetId:N}&type=authenticated", uploaded.GetProperty("url").GetString());
    }

    [Fact]
    public async Task File_that_is_not_an_accepted_image_is_refused()
    {
        var admin = await AdminAsync();

        await AccountTests.AssertErrorAsync(
            await admin.SendAsync(Upload(Encoding.UTF8.GetBytes("<svg onload=alert(1)>"), "imagem.png", "image/png")),
            HttpStatusCode.UnprocessableEntity,
            "VALIDATION_ERROR");
        await AccountTests.AssertErrorAsync(
            await admin.SendAsync(Upload([.. Png, .. new byte[5 * 1024 * 1024]], "grande.png", "image/png")),
            HttpStatusCode.UnprocessableEntity,
            "VALIDATION_ERROR");
        await AccountTests.AssertErrorAsync(
            await admin.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/v1/admin/assets") { Content = new MultipartFormDataContent { { new StringContent("x"), "outro" } } }),
            HttpStatusCode.UnprocessableEntity,
            "VALIDATION_ERROR");
    }

    [Fact]
    public async Task Essay_photo_does_not_count_as_a_prompt_image()
    {
        var admin = await AdminAsync();
        var photo = (await (await admin.PostAsync("/v1/uploads/sign", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("assetId").GetGuid();

        var draft = await admin.SendAsync(Create(ScienceDocument(NewSlug(), photo), Guid.NewGuid().ToString()));

        Assert.Contains("blocks[0].data.assetId", Paths((await draft.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("issues")));
    }

    [Fact]
    public async Task Saving_replaces_the_draft_and_returns_what_is_missing()
    {
        var admin = await AdminAsync();
        var id = await CreateAsync(admin, null);

        var saved = await admin.PutAsJsonAsync($"/v1/admin/prompts/{id}", new
        {
            title = "Algoritmos e o debate público",
            essayType = "dissertacao-em-prosa",
            blocks = new object[]
            {
                new { position = 20, kind = "command", data = new { body = "Redija uma dissertação." } },
                new { position = 10, kind = "collection_item", data = new { label = "Texto 1", format = "prose", body = "Um texto." } },
            },
        });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var body = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal([10, 20], body.GetProperty("blocks").EnumerateArray().Select(block => block.GetProperty("position").GetInt32()));
        Assert.Equal(
            ["blocks", "blocks[0].data.source", "gradingNotes.expectedFocus", "gradingNotes.tangentSignals"],
            Paths(body.GetProperty("issues")));
        var detail = await admin.GetFromJsonAsync<JsonElement>($"/v1/admin/prompts/{id}");
        Assert.Equal("Algoritmos e o debate público", detail.GetProperty("title").GetString());
        Assert.Equal(body.GetProperty("updatedAt").GetDateTimeOffset(), detail.GetProperty("updatedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Saving_with_an_old_expected_date_is_refused()
    {
        var admin = await AdminAsync();
        var id = await CreateAsync(admin, null);
        var opened = (await admin.GetFromJsonAsync<JsonElement>($"/v1/admin/prompts/{id}")).GetProperty("updatedAt").GetString();

        var first = await admin.PutAsJsonAsync($"/v1/admin/prompts/{id}", new { title = "Primeira aba", expectedUpdatedAt = opened });
        var second = await admin.PutAsJsonAsync($"/v1/admin/prompts/{id}", new { title = "Segunda aba", expectedUpdatedAt = opened });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await AccountTests.AssertErrorAsync(second, HttpStatusCode.Conflict, "PROMPT_CHANGED");
        Assert.Equal("Primeira aba", (await admin.GetFromJsonAsync<JsonElement>($"/v1/admin/prompts/{id}")).GetProperty("title").GetString());

        // Com a data que o primeiro salvamento devolveu, o seguinte passa.
        var current = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("updatedAt").GetString();
        var third = await admin.PutAsJsonAsync($"/v1/admin/prompts/{id}", new { title = "Terceira", expectedUpdatedAt = current });
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
    }

    [Fact]
    public async Task Two_saves_at_the_same_time_with_the_same_expected_date_do_not_both_pass()
    {
        var admin = await AdminAsync();
        var id = await CreateAsync(admin, null);
        var opened = (await admin.GetFromJsonAsync<JsonElement>($"/v1/admin/prompts/{id}")).GetProperty("updatedAt").GetString();
        await Task.Delay(5);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            admin.PutAsJsonAsync($"/v1/admin/prompts/{id}", new { title = $"Aba {i}", expectedUpdatedAt = opened })));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Equal(5, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task Content_that_cannot_be_stored_is_refused_with_the_field()
    {
        var admin = await AdminAsync();
        var id = await CreateAsync(admin, null);

        var markup = await admin.PutAsJsonAsync($"/v1/admin/prompts/{id}", new
        {
            blocks = new object[] { new { position = 10, kind = "command", data = new { body = "Use <b>negrito</b>." } } },
        });
        var unknownKind = await admin.PutAsJsonAsync($"/v1/admin/prompts/{id}", new
        {
            blocks = new object[] { new { position = 10, kind = "video", data = new { } } },
        });
        var unknownType = await admin.PutAsJsonAsync($"/v1/admin/prompts/{id}", new { essayType = "carta" });

        Assert.Equal(["blocks[0].data"], Paths(await AssertValidationFailedAsync(markup)));
        Assert.Equal(["blocks[0].kind"], Paths(await AssertValidationFailedAsync(unknownKind)));
        Assert.Equal(["essayType"], Paths(await AssertValidationFailedAsync(unknownType)));
        Assert.Empty((await admin.GetFromJsonAsync<JsonElement>($"/v1/admin/prompts/{id}")).GetProperty("blocks").EnumerateArray());
    }

    [Fact]
    public async Task Reordering_swaps_positions_and_refuses_repeats()
    {
        var admin = await AdminAsync();
        var id = await CreateAsync(admin, ScienceDocument(NewSlug(), null));
        var blocks = (await admin.GetFromJsonAsync<JsonElement>($"/v1/admin/prompts/{id}")).GetProperty("blocks")
            .EnumerateArray().Select(block => block.GetProperty("id").GetGuid()).ToList();
        object[] Order(params int[] positions) => [.. blocks.Zip(positions, (blockId, position) => new { id = blockId, position })];

        var swapped = await admin.PatchAsJsonAsync($"/v1/admin/prompts/{id}/order", new { blocks = Order(20, 10, 30, 40, 50, 60, 70) });
        var repeated = await admin.PatchAsJsonAsync($"/v1/admin/prompts/{id}/order", new { blocks = Order(10, 10, 30, 40, 50, 60, 70) });

        Assert.Equal(HttpStatusCode.OK, swapped.StatusCode);
        Assert.Equal(
            [blocks[1], blocks[0]],
            (await swapped.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("blocks").EnumerateArray().Take(2).Select(block => block.GetProperty("id").GetGuid()));
        Assert.Equal(["blocks"], Paths(await AssertValidationFailedAsync(repeated)));
    }

    [Fact]
    public async Task Published_prompt_is_not_saved_reordered_or_deleted()
    {
        var admin = await AdminAsync();
        var id = await PublishedAsync(admin);

        await AccountTests.AssertErrorAsync(
            await admin.PutAsJsonAsync($"/v1/admin/prompts/{id}", new { title = "Outro título" }), HttpStatusCode.Conflict, "PROMPT_NOT_DRAFT");
        await AccountTests.AssertErrorAsync(
            await admin.PatchAsJsonAsync($"/v1/admin/prompts/{id}/order", new { blocks = Array.Empty<object>() }), HttpStatusCode.Conflict, "PROMPT_NOT_DRAFT");
        await AccountTests.AssertErrorAsync(await admin.DeleteAsync($"/v1/admin/prompts/{id}"), HttpStatusCode.Conflict, "PROMPT_NOT_DRAFT");
        await AccountTests.AssertErrorAsync(await admin.PostAsync($"/v1/admin/prompts/{id}/publish", null), HttpStatusCode.Conflict, "PROMPT_NOT_DRAFT");
        Assert.Equal("published", (await admin.GetFromJsonAsync<JsonElement>($"/v1/admin/prompts/{id}")).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Draft_with_issues_is_not_published()
    {
        var admin = await AdminAsync();
        var id = await CreateAsync(admin, null);

        var response = await admin.PostAsync($"/v1/admin/prompts/{id}/publish", null);

        Assert.Contains("title", Paths(await AssertValidationFailedAsync(response)));
        Assert.Equal(PromptStatus.Draft, await api.QueryAsync(db => db.Prompts.Where(p => p.Id == id).Select(p => p.Status).SingleAsync()));
    }

    [Fact]
    public async Task Publishing_version_2_archives_version_1()
    {
        var admin = await AdminAsync();
        var first = await PublishedAsync(admin);

        var created = await admin.PostAsync($"/v1/admin/prompts/{first}/new-version", null);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var draft = await created.Content.ReadFromJsonAsync<JsonElement>();
        var second = draft.GetProperty("id").GetGuid();
        Assert.Equal(("draft", 2), (draft.GetProperty("status").GetString(), draft.GetProperty("version").GetInt32()));
        Assert.Empty(draft.GetProperty("issues").EnumerateArray());

        // Um rascunho por proposta.
        await AccountTests.AssertErrorAsync(await admin.PostAsync($"/v1/admin/prompts/{first}/new-version", null), HttpStatusCode.Conflict, "INVALID_STATE");
        await AccountTests.AssertErrorAsync(await admin.PostAsync($"/v1/admin/prompts/{second}/new-version", null), HttpStatusCode.Conflict, "INVALID_STATE");
        // O endereço não muda entre versões.
        Assert.Equal(["slug"], Paths(await AssertValidationFailedAsync(
            await admin.PutAsJsonAsync($"/v1/admin/prompts/{second}", new { slug = NewSlug() }))));

        (await admin.PostAsync($"/v1/admin/prompts/{second}/publish", null)).EnsureSuccessStatusCode();

        var versions = await api.QueryAsync(db => db.Prompts
            .Where(p => p.Id == first || p.Id == second).OrderBy(p => p.Version).Select(p => new { p.Version, p.Status }).ToListAsync());
        Assert.Equal([(1, PromptStatus.Archived), (2, PromptStatus.Published)], versions.Select(v => (v.Version, v.Status)));
        // A versão 1 continua inteira, para as redações feitas com ela.
        var old = await admin.GetFromJsonAsync<JsonElement>($"/v1/admin/prompts/{first}");
        Assert.Equal(7, old.GetProperty("blocks").GetArrayLength());
        // Arquivada, ainda dá origem a uma versão nova.
        var third = await admin.PostAsync($"/v1/admin/prompts/{first}/new-version", null);
        Assert.Equal(3, (await third.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Archiving_takes_only_a_published_prompt_out()
    {
        var admin = await AdminAsync();
        var draft = await CreateAsync(admin, null);
        var published = await PublishedAsync(admin);

        await AccountTests.AssertErrorAsync(await admin.PostAsync($"/v1/admin/prompts/{draft}/archive", null), HttpStatusCode.Conflict, "INVALID_STATE");
        var archived = await admin.PostAsync($"/v1/admin/prompts/{published}/archive", null);

        Assert.Equal("archived", (await archived.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        await AccountTests.AssertErrorAsync(await admin.PostAsync($"/v1/admin/prompts/{published}/archive", null), HttpStatusCode.Conflict, "INVALID_STATE");
        Assert.Equal(7, await api.QueryAsync(db => db.Set<PromptBlock>().CountAsync(block => block.PromptId == published)));
    }

    [Fact]
    public async Task Draft_is_deleted_with_its_blocks()
    {
        var admin = await AdminAsync();
        var id = await CreateAsync(admin, ScienceDocument(NewSlug(), null));

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/v1/admin/prompts/{id}")).StatusCode);

        await AccountTests.AssertErrorAsync(await admin.GetAsync($"/v1/admin/prompts/{id}"), HttpStatusCode.NotFound, "NOT_FOUND");
        await AccountTests.AssertErrorAsync(await admin.DeleteAsync($"/v1/admin/prompts/{id}"), HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.Equal(0, await api.QueryAsync(db => db.Set<PromptBlock>().CountAsync(block => block.PromptId == id)));
    }

    [Fact]
    public async Task Imported_slug_that_already_exists_is_refused()
    {
        var admin = await AdminAsync();
        var slug = NewSlug();
        await CreateAsync(admin, ScienceDocument(slug, null));

        var again = await admin.SendAsync(Create(ScienceDocument(slug, null), Guid.NewGuid().ToString()));

        Assert.Equal(["slug"], Paths(await AssertValidationFailedAsync(again)));
    }

    [Fact]
    public async Task List_filters_by_status_and_title_and_counts_every_status()
    {
        var admin = await AdminAsync();
        var mark = $"Marca{Guid.NewGuid():N}";
        var published = await PublishedAsync(admin, title: $"{mark} publicada");
        var draft = await CreateAsync(admin, new JsonObject { ["title"] = $"{mark} rascunho" });
        var archived = await PublishedAsync(admin, title: $"{mark} arquivada");
        (await admin.PostAsync($"/v1/admin/prompts/{archived}/archive", null)).EnsureSuccessStatusCode();

        var all = await admin.GetFromJsonAsync<JsonElement>($"/v1/admin/prompts?q={mark.ToLowerInvariant()}");
        var drafts = await admin.GetFromJsonAsync<JsonElement>($"/v1/admin/prompts?q={mark}&status=draft&exam=fuvest");

        Assert.Equal((3, 1, 1, 1), (
            all.GetProperty("counts").GetProperty("all").GetInt32(),
            all.GetProperty("counts").GetProperty("draft").GetInt32(),
            all.GetProperty("counts").GetProperty("published").GetInt32(),
            all.GetProperty("counts").GetProperty("archived").GetInt32()));
        Assert.Equal(
            new[] { published, draft, archived }.Order(),
            all.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).Order());
        var item = all.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == published);
        Assert.Equal(("Fuvest", "Dissertação em prosa", 5, 1), (
            item.GetProperty("examName").GetString(),
            item.GetProperty("essayTypeName").GetString(),
            item.GetProperty("textCount").GetInt32(),
            item.GetProperty("version").GetInt32()));
        Assert.Equal(draft, Assert.Single(drafts.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        // O filtro de situação não muda as contagens das abas.
        Assert.Equal(3, drafts.GetProperty("counts").GetProperty("all").GetInt32());

        await AccountTests.AssertErrorAsync(await admin.GetAsync("/v1/admin/prompts?status=apagada"), HttpStatusCode.UnprocessableEntity, "VALIDATION_ERROR");
        // O que se busca vale literalmente.
        Assert.Empty((await admin.GetFromJsonAsync<JsonElement>($"/v1/admin/prompts?q={mark}%25")).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Every_write_goes_to_the_audit_log()
    {
        var admin = await AdminAsync();
        var first = await PublishedAsync(admin);
        var second = (await (await admin.PostAsync($"/v1/admin/prompts/{first}/new-version", null)).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();
        (await admin.PutAsJsonAsync($"/v1/admin/prompts/{second}", new { title = "Rascunho descartado" })).EnsureSuccessStatusCode();
        (await admin.DeleteAsync($"/v1/admin/prompts/{second}")).EnsureSuccessStatusCode();
        (await admin.PostAsync($"/v1/admin/prompts/{first}/archive", null)).EnsureSuccessStatusCode();

        var entries = await api.QueryAsync(db => db.AuditLog
            .Where(a => a.Entity == "prompt" && (a.EntityId == first || a.EntityId == second))
            .Select(a => new { a.Action, a.Actor, a.EntityId })
            .ToListAsync());

        Assert.Equal(
            ["prompt.archived", "prompt.created", "prompt.published"],
            entries.Where(e => e.EntityId == first).Select(e => e.Action).Order());
        Assert.Equal(
            ["prompt.deleted", "prompt.updated", "prompt.version_created"],
            entries.Where(e => e.EntityId == second).Select(e => e.Action).Order());
        var adminId = await api.QueryAsync(db => db.Prompts.Where(p => p.Id == first).Select(p => p.CreatedBy).SingleAsync());
        Assert.All(entries, entry => Assert.Equal($"user:{adminId}", entry.Actor));
    }

    private static string NewSlug() => $"proposta-{Guid.NewGuid():N}";

    private static List<string> Paths(JsonElement issues) =>
        [.. issues.EnumerateArray().Select(issue => issue.GetProperty("path").GetString()!)];

    private static async Task<JsonElement> AssertValidationFailedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("VALIDATION_FAILED", error.GetProperty("code").GetString());
        Assert.All(error.GetProperty("details").EnumerateArray(), detail => Assert.False(string.IsNullOrEmpty(detail.GetProperty("message").GetString())));
        return error.GetProperty("details");
    }

    private static HttpRequestMessage Request(string method, string path, string body = "{}")
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (path.EndsWith("/assets", StringComparison.Ordinal))
        {
            request.Content = new MultipartFormDataContent { { new ByteArrayContent(Png), "file", "imagem.png" } };
        }
        else if (method is "POST" or "PUT" or "PATCH")
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return request;
    }

    private static HttpRequestMessage Create(JsonNode? document, string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/admin/prompts")
        {
            Content = new StringContent(document?.ToJsonString() ?? "{}", Encoding.UTF8, "application/json"),
        };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return request;
    }

    private static HttpRequestMessage Upload(byte[] content, string fileName, string contentType)
    {
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new HttpRequestMessage(HttpMethod.Post, "/v1/admin/assets") { Content = new MultipartFormDataContent { { file, "file", fileName } } };
    }

    private static async Task<JsonElement> UploadAsync(HttpClient admin, byte[] content, string fileName, string contentType = "image/png")
    {
        var response = await admin.SendAsync(Upload(content, fileName, contentType));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<Guid> CreateAsync(HttpClient admin, JsonNode? document)
    {
        var response = await admin.SendAsync(Create(document, Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<Guid> PublishedAsync(HttpClient admin, string? title = null)
    {
        var image = await UploadAsync(admin, Png, "tirinha.png");
        var document = ScienceDocument(NewSlug(), image.GetProperty("assetId").GetGuid());
        if (title is not null)
        {
            document["title"] = title;
        }

        var id = await CreateAsync(admin, document);
        (await admin.PostAsync($"/v1/admin/prompts/{id}/publish", null)).EnsureSuccessStatusCode();
        return id;
    }

    /// <summary>O arquivo de importação da proposta da ciência, como está em docs/fontes/propostas.</summary>
    private static JsonObject ScienceDocument(string slug, Guid? imageId)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docs", "fontes", "propostas", "papel-da-ciencia.json")))
        {
            directory = directory.Parent;
        }

        var path = Path.Combine(directory!.FullName, "docs", "fontes", "propostas", "papel-da-ciencia.json");
        var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        document["slug"] = slug;
        document["blocks"]![0]!["data"]!["assetId"] = imageId?.ToString();
        return document;
    }

    /// <summary>O que tem de ser igual entre o exportado e o importado de novo: tudo menos os campos de controle.</summary>
    private static string Comparable(JsonObject prompt)
    {
        var copy = prompt.DeepClone().AsObject();
        foreach (var field in new[] { "id", "version", "status", "updatedAt", "publishedAt", "issues" })
        {
            copy.Remove(field);
        }

        foreach (var block in copy["blocks"]!.AsArray())
        {
            block!.AsObject().Remove("id");
        }

        return copy.ToJsonString();
    }

    private async Task<string> RegisterAdminAsync()
    {
        var userId = ApiFixture.NewClerkUserId();
        var client = api.CreateClient(api.CreateToken(userId, email: ApiFixture.AdminEmail));
        (await client.PostAsJsonAsync("/v1/me", new { birthDate = "2000-05-17", termsAccepted = true })).EnsureSuccessStatusCode();
        await api.MakeAdminAsync(userId);
        return userId;
    }

    private async Task<HttpClient> AdminAsync() =>
        api.CreateClient(api.CreateToken(await RegisterAdminAsync(), email: ApiFixture.AdminEmail));

    private async Task<HttpClient> StudentAsync()
    {
        var client = api.CreateClient(api.CreateToken(ApiFixture.NewClerkUserId()));
        (await client.PostAsJsonAsync("/v1/me", new { birthDate = "2000-05-17", termsAccepted = true })).EnsureSuccessStatusCode();
        return client;
    }
}
