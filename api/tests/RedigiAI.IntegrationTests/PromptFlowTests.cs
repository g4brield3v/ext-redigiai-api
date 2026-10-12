using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RedigiAI.Application.Abstractions;
using RedigiAI.Application.Billing;
using RedigiAI.Domain.Billing;
using RedigiAI.Domain.Jobs;

namespace RedigiAI.IntegrationTests;

/// <summary>
/// O caminho do aluno sobre as propostas cadastradas pelo administrador (docs/07-propostas-de-redacao.md, A7 e A8):
/// a lista, a página, o envio sem escolha de tipo e as versões.
/// </summary>
[Collection(ApiCollection.Name)]
public class PromptFlowTests(ApiFixture api)
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];

    [Fact]
    public async Task Published_prompt_reaches_the_student_without_what_is_only_for_the_ai()
    {
        var admin = await AdminAsync();
        var (id, slug) = await PublishAsync(admin, "Proposta para o aluno");

        var listed = (await api.CreateClient().GetFromJsonAsync<JsonElement>("/v1/exams/fuvest/prompts"))
            .EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == id);
        var raw = await api.CreateClient().GetStringAsync($"/v1/prompts/{slug}");
        var prompt = JsonDocument.Parse(raw).RootElement;

        Assert.Equal(("Proposta para o aluno", "Dissertação em prosa", 5), (
            listed.GetProperty("title").GetString(), listed.GetProperty("genreLabel").GetString(), listed.GetProperty("textCount").GetInt32()));
        Assert.Equal("Coletânea com tirinha, poema e três textos em prosa.", listed.GetProperty("summary").GetString());
        Assert.Equal(id, prompt.GetProperty("id").GetGuid());
        Assert.Equal("Fuvest – Dissertação – Proposta para o aluno", prompt.GetProperty("essayTitle").GetString());

        // A imagem chega pelo endereço público, com o texto alternativo.
        var strip = prompt.GetProperty("blocks")[0].GetProperty("data");
        Assert.StartsWith("https://res.cloudinary.com/test/image/upload/prompts/", strip.GetProperty("imageUrl").GetString());
        Assert.False(string.IsNullOrEmpty(strip.GetProperty("alt").GetString()));

        // As abordagens esperadas, a descrição da imagem para a IA e os ids internos não saem.
        Assert.DoesNotContain("gradingNotes", raw);
        Assert.DoesNotContain("expectedFocus", raw);
        Assert.DoesNotContain("aiDescription", raw);
        Assert.DoesNotContain("assetId", raw);
        Assert.DoesNotContain("Ponto de vista sobre a função da ciência", raw);
        Assert.DoesNotContain("Quadro 1", raw);
    }

    [Fact]
    public async Task Draft_is_invisible_to_students()
    {
        var admin = await AdminAsync();
        var slug = NewSlug();
        var draft = await CreateAsync(admin, ScienceDocument(slug, await UploadAsync(admin), "Rascunho escondido"));
        var student = await StudentAsync();

        Assert.DoesNotContain(
            (await student.Client.GetFromJsonAsync<JsonElement>("/v1/exams/fuvest/prompts")).EnumerateArray(),
            p => p.GetProperty("id").GetGuid() == draft);
        await AccountTests.AssertErrorAsync(await student.Client.GetAsync($"/v1/prompts/{slug}"), HttpStatusCode.NotFound, "PROMPT_NOT_AVAILABLE");
        await AccountTests.AssertErrorAsync(
            await student.Client.GetAsync($"/v1/prompts/{slug}?version=1"), HttpStatusCode.NotFound, "PROMPT_NOT_AVAILABLE");
        await AccountTests.AssertErrorAsync(await student.SubmitAsync(draft), HttpStatusCode.NotFound, "PROMPT_NOT_AVAILABLE");
        Assert.Equal(1, await student.BalanceAsync());
    }

    [Fact]
    public async Task Essay_always_gets_the_type_of_the_prompt()
    {
        var admin = await AdminAsync();
        var (promptId, _) = await PublishAsync(admin, "Tipo vem da proposta");
        var student = await StudentAsync();
        var narrativa = await api.QueryAsync(db => db.EssayTypes.Where(t => t.Slug == "narrativa").Select(t => t.Id).SingleAsync());
        var dissertacao = await api.QueryAsync(db => db.EssayTypes.Where(t => t.Slug == "dissertacao").Select(t => t.Id).SingleAsync());

        // Um tipo enviado pelo navegador não vale: o backend usa o da proposta.
        var response = await student.SubmitAsync(promptId, extra: new JsonObject { ["essayTypeId"] = narrativa.ToString(), ["essayType"] = "narrativa" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var essayId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("essayIds")[0].GetGuid();
        var essay = await api.QueryAsync(db => db.Essays.SingleAsync(e => e.Id == essayId));
        Assert.Equal((promptId, dissertacao, "Fuvest – Dissertação – Tipo vem da proposta"), (essay.PromptId, essay.EssayTypeId, essay.Title));
    }

    [Fact]
    public async Task Essay_made_on_version_1_keeps_showing_version_1()
    {
        var admin = await AdminAsync();
        var (first, slug) = await PublishAsync(admin, "Tema na versão 1");
        var student = await StudentAsync(extraCredits: 3);
        var essayId = await student.SubmittedAsync(first);

        // O administrador corrige a proposta: a versão 2 entra no lugar e a 1 é arquivada.
        var second = (await (await admin.PostAsync($"/v1/admin/prompts/{first}/new-version", null)).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();
        var changed = JsonNode.Parse(await admin.GetStringAsync($"/v1/admin/prompts/{second}"))!.AsObject();
        changed["title"] = "Tema na versão 2";
        (await admin.PutAsync($"/v1/admin/prompts/{second}", Json(changed))).EnsureSuccessStatusCode();
        (await admin.PostAsync($"/v1/admin/prompts/{second}/publish", null)).EnsureSuccessStatusCode();

        // A redação continua na versão 1, com o título com que foi enviada.
        var essay = await student.Client.GetFromJsonAsync<JsonElement>($"/v1/essays/{essayId}");
        Assert.Equal((first, slug, 1), (
            essay.GetProperty("promptId").GetGuid(), essay.GetProperty("promptSlug").GetString(), essay.GetProperty("promptVersion").GetInt32()));
        Assert.Equal("Fuvest – Dissertação – Tema na versão 1", essay.GetProperty("title").GetString());
        var listed = (await student.Client.GetFromJsonAsync<JsonElement>("/v1/essays")).GetProperty("items")[0];
        Assert.Equal((slug, 1), (listed.GetProperty("promptSlug").GetString(), listed.GetProperty("promptVersion").GetInt32()));

        // O endereço mostra a versão 2; a versão 1 continua disponível para quem a usou.
        var current = await student.Client.GetFromJsonAsync<JsonElement>($"/v1/prompts/{slug}");
        var old = await student.Client.GetFromJsonAsync<JsonElement>($"/v1/prompts/{slug}?version=1");
        Assert.Equal((second, 2, "published", "Tema na versão 2"), (
            current.GetProperty("id").GetGuid(), current.GetProperty("version").GetInt32(),
            current.GetProperty("status").GetString(), current.GetProperty("title").GetString()));
        Assert.Equal((first, 1, "archived", "Tema na versão 1"), (
            old.GetProperty("id").GetGuid(), old.GetProperty("version").GetInt32(),
            old.GetProperty("status").GetString(), old.GetProperty("title").GetString()));
        Assert.Single(
            (await student.Client.GetFromJsonAsync<JsonElement>("/v1/exams/fuvest/prompts")).EnumerateArray(),
            p => p.GetProperty("slug").GetString() == slug);

        // A reescrita usa a versão da redação original, venha o id da versão antiga ou o da nova.
        foreach (var sent in new[] { second, first })
        {
            var rewrite = await student.SubmitAsync(sent, parentEssayId: essayId);
            Assert.Equal(HttpStatusCode.Accepted, rewrite.StatusCode);
            var rewriteId = (await rewrite.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("essayIds")[0].GetGuid();
            var saved = await api.QueryAsync(db => db.Essays.SingleAsync(e => e.Id == rewriteId));
            Assert.Equal((first, essayId, "Fuvest – Dissertação – Tema na versão 1"), (saved.PromptId, saved.ParentEssayId, saved.Title));
        }

        // Uma redação nova não entra na versão arquivada, e uma reescrita não muda de proposta.
        await AccountTests.AssertErrorAsync(await student.SubmitAsync(first), HttpStatusCode.NotFound, "PROMPT_NOT_AVAILABLE");
        var (other, _) = await PublishAsync(admin, "Outra proposta");
        await AccountTests.AssertErrorAsync(
            await student.SubmitAsync(other, parentEssayId: essayId), HttpStatusCode.NotFound, "ESSAY_NOT_FOUND");
        Assert.Equal(1, await student.BalanceAsync());
    }

    [Fact]
    public async Task Grading_receives_every_kind_of_block_as_text_in_page_order()
    {
        var admin = await AdminAsync();
        var slug = NewSlug();
        var mark = $"Tema {Guid.NewGuid():N}";
        var document = ScienceDocument(slug, await UploadAsync(admin), $"{mark} com \"aspas\"");
        var blocks = document["blocks"]!.AsArray();
        blocks.Add(new JsonObject { ["position"] = 5, ["kind"] = "heading", ["data"] = new JsonObject { ["text"] = "Coletânea de apoio" } });
        blocks.Add(new JsonObject { ["position"] = 55, ["kind"] = "divider", ["data"] = new JsonObject() });
        // Um parágrafo que imita um marcador das instruções: tem de chegar como texto, sem ser preenchido.
        blocks.Add(new JsonObject { ["position"] = 65, ["kind"] = "paragraph", ["data"] = new JsonObject { ["body"] = "Leia com atenção {{numbered_lines}} antes de escrever." } });
        var id = await CreateAsync(admin, document);
        (await admin.PostAsync($"/v1/admin/prompts/{id}/publish", null)).EnsureSuccessStatusCode();
        var student = await StudentAsync();
        api.Transcriber.Script.Clear();
        await student.SubmittedAsync(id);

        await RunJobsAsync();

        var request = api.Grader.Requests.Last(r => r.Proposal.Contains(mark));
        var proposal = request.Proposal.ReplaceLineEndings("\n");
        // Aspas no título não fecham o atributo.
        Assert.StartsWith($"<proposta titulo=\"{mark} com 'aspas'\" tipo=\"Dissertação em prosa\" linhas_min=\"20\" linhas_max=\"30\" titulo_obrigatorio=\"sim\">\n", proposal);
        // Da imagem vai a descrição feita para a IA; o verso vai com as colunas separadas.
        Assert.Contains("[Texto 1 · imagem] Tirinha em três quadros, com o título", proposal);
        Assert.Contains("— Luis Fernando Verissimo, *As cobras: Antologia Definitiva*.\n[Texto 2] Somente numa sociedade", proposal);
        Assert.Contains("[Texto 3 · verso] Quanta do latim\nPlural de quantum", proposal);
        Assert.Contains("Qualquer coisa quase ideal / Cântico dos cânticos", proposal);
        // Título de seção e separador não vão; o parágrafo vai, e o que parece marcador continua sendo texto.
        Assert.DoesNotContain("Coletânea de apoio", proposal);
        Assert.Contains("\n[Comando] Considerando as ideias apresentadas nos textos", proposal);
        Assert.Contains("\nLeia com atenção {{numbered_lines}} antes de escrever.\n[Instruções] Tipo de redação: Dissertação em prosa; A dissertação deve ser redigida", proposal);
        Assert.DoesNotContain("1| ", proposal);
        // As abordagens esperadas, com os quatro campos.
        Assert.Contains("<abordagens_esperadas>\nRecorte esperado: Ponto de vista sobre a função da ciência hoje", proposal);
        Assert.Contains("\nSinais de tangenciamento: Fala de tecnologia ou de inovação em geral", proposal);
        Assert.Contains("\nSinais de fuga ao tema: Não menciona ciência nem conhecimento científico.", proposal);
        Assert.Contains("\nDados-chave da coletânea: Texto 2: a ciência só floresce", proposal);
        Assert.Contains("; Texto 3: arte e ciência como formas irmãs de conhecimento.\n</abordagens_esperadas>", proposal);
        Assert.StartsWith("<redacao linhas=\"2\"", request.Essay);
    }

    [Fact]
    public async Task Archived_prompt_leaves_the_list_and_refuses_new_essays()
    {
        var admin = await AdminAsync();
        var (id, slug) = await PublishAsync(admin, "Proposta que sai da lista");
        var student = await StudentAsync();
        (await admin.PostAsync($"/v1/admin/prompts/{id}/archive", null)).EnsureSuccessStatusCode();

        Assert.DoesNotContain(
            (await student.Client.GetFromJsonAsync<JsonElement>("/v1/exams/fuvest/prompts")).EnumerateArray(),
            p => p.GetProperty("id").GetGuid() == id);
        await AccountTests.AssertErrorAsync(await student.Client.GetAsync($"/v1/prompts/{slug}"), HttpStatusCode.NotFound, "PROMPT_NOT_AVAILABLE");
        await AccountTests.AssertErrorAsync(await student.SubmitAsync(id), HttpStatusCode.NotFound, "PROMPT_NOT_AVAILABLE");
        Assert.Equal(HttpStatusCode.OK, (await student.Client.GetAsync($"/v1/prompts/{slug}?version=1")).StatusCode);
    }

    private async Task RunJobsAsync()
    {
        var runner = api.Factory.Services.GetRequiredService<IJobRunner>();
        foreach (var type in new[] { JobTypes.Transcribe, JobTypes.Grade })
        {
            while (await runner.RunNextAsync([type], CancellationToken.None))
            {
            }
        }
    }

    private static string NewSlug() => $"proposta-{Guid.NewGuid():N}";

    private static StringContent Json(JsonNode node) => new(node.ToJsonString(), Encoding.UTF8, "application/json");

    private static JsonObject ScienceDocument(string slug, Guid imageId, string title)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docs", "fontes", "propostas", "papel-da-ciencia.json")))
        {
            directory = directory.Parent;
        }

        var document = JsonNode.Parse(File.ReadAllText(Path.Combine(directory!.FullName, "docs", "fontes", "propostas", "papel-da-ciencia.json")))!.AsObject();
        document["slug"] = slug;
        document["title"] = title;
        document["blocks"]![0]!["data"]!["assetId"] = imageId.ToString();
        return document;
    }

    private static async Task<Guid> UploadAsync(HttpClient admin)
    {
        var response = await admin.PostAsync(
            "/v1/admin/assets", new MultipartFormDataContent { { new ByteArrayContent(Png), "file", "tirinha.png" } });
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("assetId").GetGuid();
    }

    private static async Task<Guid> CreateAsync(HttpClient admin, JsonObject document)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/admin/prompts") { Content = Json(document) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await admin.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<(Guid Id, string Slug)> PublishAsync(HttpClient admin, string title)
    {
        var slug = NewSlug();
        var id = await CreateAsync(admin, ScienceDocument(slug, await UploadAsync(admin), title));
        (await admin.PostAsync($"/v1/admin/prompts/{id}/publish", null)).EnsureSuccessStatusCode();
        return (id, slug);
    }

    private async Task<HttpClient> AdminAsync()
    {
        var userId = ApiFixture.NewClerkUserId();
        api.Directory.Accounts[userId] = new PrimaryEmail(ApiFixture.AdminEmail, Verified: true);
        var client = api.CreateClient(api.CreateToken(userId, email: ApiFixture.AdminEmail, secondFactor: true));
        (await client.PostAsJsonAsync("/v1/me", new { birthDate = "2000-05-17", termsAccepted = true })).EnsureSuccessStatusCode();
        return client;
    }

    private async Task<Student> StudentAsync(int extraCredits = 0)
    {
        var client = api.CreateClient(api.CreateToken(ApiFixture.NewClerkUserId()));
        var profile = await (await client.PostAsJsonAsync("/v1/me", new { birthDate = "2000-05-17", termsAccepted = true }))
            .Content.ReadFromJsonAsync<JsonElement>();
        if (extraCredits > 0)
        {
            await using var scope = api.Factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ICreditService>().GrantAsync(
                new GrantCreditsCommand(profile.GetProperty("id").GetGuid(), CreditSource.Single, extraCredits, null, "test"), CancellationToken.None);
        }

        return new Student(api, client);
    }

    private sealed class Student(ApiFixture api, HttpClient client)
    {
        public HttpClient Client { get; } = client;

        public async Task<int> BalanceAsync() =>
            (await Client.GetFromJsonAsync<JsonElement>("/v1/me")).GetProperty("credits").GetProperty("total").GetInt32();

        public async Task<HttpResponseMessage> SubmitAsync(Guid promptId, Guid? parentEssayId = null, JsonObject? extra = null)
        {
            var assetId = (await (await Client.PostAsync("/v1/uploads/sign", null)).Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("assetId").GetGuid();
            var publicId = await api.QueryAsync(db => db.Assets.Where(a => a.Id == assetId).Select(a => a.PublicId).SingleAsync());
            api.Photos.Stored[publicId] = new StoredPhoto(1024, "jpg");

            var item = extra ?? [];
            item["promptId"] = promptId.ToString();
            item["assetId"] = assetId.ToString();
            if (parentEssayId is { } parent)
            {
                item["parentEssayId"] = parent.ToString();
            }

            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/essays/batch")
            {
                Content = Json(new JsonObject { ["essays"] = new JsonArray(item), ["aiNoticeAccepted"] = true }),
            };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            return await Client.SendAsync(request);
        }

        public async Task<Guid> SubmittedAsync(Guid promptId)
        {
            var response = await SubmitAsync(promptId);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("essayIds")[0].GetGuid();
        }
    }
}
