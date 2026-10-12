using RedigiAI.Domain.Catalog;
using RedigiAI.Domain.Common;

namespace RedigiAI.UnitTests.Catalog;

/// <summary>Regras das propostas de redação (docs/07-propostas-de-redacao.md, A3 e A4).</summary>
public class PromptTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 15, 0, 0, TimeSpan.Zero);
    private static readonly Guid ExamId = Guid.CreateVersion7();
    private static readonly Guid EssayTypeId = Guid.CreateVersion7();
    private static readonly Guid ImageId = Guid.CreateVersion7();
    private static readonly PublicationContext Context = new(true, 30, new HashSet<Guid> { ImageId });

    private const string AiDescription = "Tirinha em três quadros: duas cobras numa ladeira e uma roda que desce.";

    private static PromptBlockContent Image(int position = 10, string? label = "Texto 1") => new(
        null,
        position,
        PromptBlockKind.CollectionItem,
        new PromptBlockData
        {
            Label = label,
            Format = CollectionFormat.Image,
            Source = "Luis Fernando Verissimo, *As cobras*.",
            AssetId = ImageId,
            Alt = "Tirinha com duas cobras e uma roda.",
            AiDescription = AiDescription,
        });

    private static PromptBlockContent Prose(int position = 20, string? label = "Texto 2", string? body = "Somente numa sociedade...") => new(
        null,
        position,
        PromptBlockKind.CollectionItem,
        new PromptBlockData { Label = label, Format = CollectionFormat.Prose, Source = "Oscar Sala, 1974.", Body = body });

    private static PromptBlockContent Verse(int position = 30, params string[][] columns) => new(
        null,
        position,
        PromptBlockKind.CollectionItem,
        new PromptBlockData
        {
            Label = "Texto 3",
            Format = CollectionFormat.Verse,
            Source = "Gilberto Gil, *Quanta*. 1997.",
            Columns = columns.Length == 0 ? [["Quanta do latim", "Plural de quantum"], ["Cântico dos cânticos"]] : columns,
        });

    private static PromptBlockContent Command(int position = 60, string? body = "Redija uma dissertação em prosa sobre **o papel da ciência**.") =>
        new(null, position, PromptBlockKind.Command, new PromptBlockData { Body = body });

    private static PromptBlockContent Instructions(int position = 70) => new(
        null,
        position,
        PromptBlockKind.Instructions,
        new PromptBlockData { Title = "Instruções", Items = ["Escreva, no mínimo, 20 linhas.", "Dê um título a sua redação."] });

    private static PromptNotesContent Notes { get; } = new(
        "Ponto de vista sobre a função da ciência hoje.",
        "Fala de tecnologia em geral sem discutir o papel da ciência.",
        "Não menciona ciência.",
        ["Texto 2: a ciência só floresce numa cultura que valoriza a curiosidade."],
        null);

    /// <summary>A proposta da ciência, completa: publica sem pendências.</summary>
    private static PromptContent Complete(params PromptBlockContent[] blocks) => new(
        ExamId,
        "papel-da-ciencia",
        "O papel da ciência no mundo contemporâneo",
        "Coletânea com tirinha, poema e três textos em prosa.",
        "Fuvest (prova anterior)",
        EssayTypeId,
        "Dissertação em prosa",
        20,
        30,
        TitleRequired: true,
        blocks.Length == 0 ? [Image(), Prose(), Verse(), Command(), Instructions()] : blocks,
        Notes);

    private static IReadOnlyList<ValidationIssue> IssuesOf(PromptContent content, PublicationContext? context = null) =>
        Prompt.Draft(content, null, Now).PublicationIssues(context ?? Context);

    private static void AssertIssue(PromptContent content, string path, PublicationContext? context = null) =>
        Assert.Contains(IssuesOf(content, context), issue => issue.Path == path);

    private static AppError AssertRefused(Action action, string path)
    {
        var error = Assert.Throws<AppError>(action);
        Assert.Equal("VALIDATION_FAILED", error.Code);
        Assert.Equal(422, error.HttpStatus);
        Assert.Contains(error.Details!, issue => issue.Path == path);
        return error;
    }

    [Fact]
    public void Complete_prompt_has_no_issues_and_is_published()
    {
        var prompt = Prompt.Draft(Complete(), null, Now);

        Assert.Empty(prompt.PublicationIssues(Context));
        prompt.Publish(Context, null, Now.AddMinutes(1));

        Assert.Equal(PromptStatus.Published, prompt.Status);
        Assert.Equal(Now.AddMinutes(1), prompt.PublishedAt);
        Assert.Equal(7, prompt.Id.Version);
        Assert.Equal([10, 20, 30, 60, 70], prompt.Blocks.Select(block => block.Position));
    }

    [Fact]
    public void Empty_draft_is_saved_and_lists_what_is_missing()
    {
        var empty = new PromptContent(ExamId, "rascunho", null, null, null, null, null, null, null, false, [], PromptNotesContent.Empty);

        var issues = IssuesOf(empty).Select(issue => issue.Path).ToList();

        Assert.Equal(
            ["title", "essayType", "genreLabel", "sheet", "blocks", "blocks", "blocks", "gradingNotes.expectedFocus", "gradingNotes.tangentSignals"],
            issues);
    }

    [Fact]
    public void Draft_with_issues_is_not_published()
    {
        var prompt = Prompt.Draft(Complete() with { GenreLabel = null }, null, Now);

        AssertRefused(() => prompt.Publish(Context, null, Now), "genreLabel");
        Assert.Equal(PromptStatus.Draft, prompt.Status);
    }

    // Regra 1: título de 3 a 120 caracteres; endereço em minúsculas, números e hífen.
    [Theory]
    [InlineData("Oi", false)]
    [InlineData("Mar", true)]
    public void Title_needs_at_least_3_characters(string title, bool accepted) =>
        Assert.Equal(accepted, IssuesOf(Complete() with { Title = title }).All(issue => issue.Path != "title"));

    [Fact]
    public void Title_with_more_than_120_characters_is_not_even_saved()
    {
        Assert.Empty(IssuesOf(Complete() with { Title = new string('a', 120) }));
        AssertRefused(() => Prompt.Draft(Complete() with { Title = new string('a', 121) }, null, Now), "title");
    }

    [Theory]
    [InlineData("papel-da-ciencia", true)]
    [InlineData("tema-2", true)]
    [InlineData("Papel-da-ciencia", false)]
    [InlineData("papel da ciencia", false)]
    [InlineData("papel_da_ciencia", false)]
    [InlineData("papel-da-ciência", false)]
    [InlineData("-papel", false)]
    [InlineData("papel--ciencia", false)]
    public void Slug_has_only_lowercase_letters_digits_and_hyphens(string slug, bool accepted) =>
        Assert.Equal(accepted, IssuesOf(Complete() with { Slug = slug }).All(issue => issue.Path != "slug"));

    // Regra 2: categoria de correção com perfil ativo; gênero de 3 a 80 caracteres.
    [Fact]
    public void Essay_type_needs_an_active_grading_profile()
    {
        AssertIssue(Complete() with { EssayTypeId = null }, "essayType");
        AssertIssue(Complete(), "essayType", Context with { EssayTypeHasActiveProfile = false });
        Assert.Empty(IssuesOf(Complete()));
    }

    [Theory]
    [InlineData("Dissertação em prosa", true)]
    [InlineData("Ata", true)]
    [InlineData("Ab", false)]
    [InlineData(null, false)]
    public void Genre_label_has_3_to_80_characters(string? genre, bool accepted) =>
        Assert.Equal(accepted, IssuesOf(Complete() with { GenreLabel = genre }).All(issue => issue.Path != "genreLabel"));

    [Fact]
    public void Genre_label_with_more_than_80_characters_is_not_even_saved()
    {
        Assert.Empty(IssuesOf(Complete() with { GenreLabel = new string('a', 80) }));
        AssertRefused(() => Prompt.Draft(Complete() with { GenreLabel = new string('a', 81) }, null, Now), "genreLabel");
    }

    // Regra 3: ao menos um texto da coletânea, exatamente um bloco de instruções e ao menos um comando.
    [Fact]
    public void Needs_a_collection_item_a_command_and_exactly_one_instructions_block()
    {
        AssertIssue(Complete(Command(), Instructions()), "blocks");
        AssertIssue(Complete(Prose(), Instructions()), "blocks");
        AssertIssue(Complete(Prose(), Command()), "blocks");
        AssertIssue(Complete(Prose(), Command(), Instructions(70), Instructions(80)), "blocks");
        Assert.Empty(IssuesOf(Complete(Prose(), Command(), Command(65), Instructions())));
    }

    // Regra 4: posição única por proposta.
    [Fact]
    public void Two_blocks_cannot_share_a_position()
    {
        AssertRefused(() => Prompt.Draft(Complete(Prose(20), Command(20), Instructions()), null, Now), "blocks");
        AssertRefused(() => Prompt.Draft(Complete(Prose(-1), Command(), Instructions()), null, Now), "blocks");
        Assert.Empty(IssuesOf(Complete(Prose(0), Command(1), Instructions(2))));
    }

    // Regra 5: rótulo e fonte obrigatórios, rótulo único, e os campos de cada formato.
    [Fact]
    public void Collection_item_needs_label_and_source_and_a_unique_label()
    {
        AssertIssue(Complete(Prose(label: null), Command(), Instructions()), "blocks[0].data.label");
        AssertIssue(Complete(Prose(10, "Texto 1"), Prose(20, "texto 1"), Command(), Instructions()), "blocks[1].data.label");
        var noSource = Prose() with { Data = Prose().Data with { Source = " " } };
        AssertIssue(Complete(noSource, Command(), Instructions()), "blocks[0].data.source");
        Assert.Empty(IssuesOf(Complete(Prose(10, "Texto 1"), Prose(20, "Texto 2"), Command(), Instructions())));
    }

    [Fact]
    public void Prose_needs_the_body()
    {
        AssertIssue(Complete(Prose(body: "  "), Command(), Instructions()), "blocks[0].data.body");
        Assert.Empty(IssuesOf(Complete(Prose(body: "Texto."), Command(), Instructions())));
    }

    [Fact]
    public void Verse_needs_1_to_4_columns_with_at_least_one_verse_each()
    {
        var noColumns = Verse() with { Data = Verse().Data with { Columns = [] } };
        AssertIssue(Complete(noColumns, Command(), Instructions()), "blocks[0].data.columns");
        AssertIssue(Complete(Verse(30, ["Quanta do latim"], [" "]), Command(), Instructions()), "blocks[0].data.columns");
        Assert.Empty(IssuesOf(Complete(Verse(30, ["Quanta do latim"]), Command(), Instructions())));
        Assert.Empty(IssuesOf(Complete(Verse(30, ["a"], ["b"], ["c"], ["d"]), Command(), Instructions())));
        AssertRefused(
            () => Prompt.Draft(Complete(Verse(30, ["a"], ["b"], ["c"], ["d"], ["e"]), Command(), Instructions()), null, Now),
            "blocks[0].data.columns");
    }

    [Fact]
    public void Image_needs_a_prompt_image_alt_text_and_ai_description()
    {
        PromptBlockContent With(Func<PromptBlockData, PromptBlockData> change) => Image() with { Data = change(Image().Data) };

        AssertIssue(Complete(With(d => d with { AssetId = null }), Command(), Instructions()), "blocks[0].data.assetId");
        // A imagem precisa existir como imagem de proposta: o id de uma foto de redação não serve.
        AssertIssue(Complete(With(d => d with { AssetId = Guid.CreateVersion7() }), Command(), Instructions()), "blocks[0].data.assetId");
        AssertIssue(Complete(With(d => d with { Alt = null }), Command(), Instructions()), "blocks[0].data.alt");
        AssertIssue(Complete(With(d => d with { Alt = new string('a', 301) }), Command(), Instructions()), "blocks[0].data.alt");
        AssertIssue(Complete(With(d => d with { AiDescription = new string('a', 39) }), Command(), Instructions()), "blocks[0].data.aiDescription");
        AssertIssue(Complete(With(d => d with { AiDescription = new string('a', 3001) }), Command(), Instructions()), "blocks[0].data.aiDescription");

        var limits = With(d => d with { Alt = new string('a', 300), AiDescription = new string('a', 40) });
        Assert.Empty(IssuesOf(Complete(limits, Command(), Instructions())));
        Assert.Empty(IssuesOf(Complete(With(d => d with { AiDescription = new string('a', 3000) }), Command(), Instructions())));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("video")]
    public void Collection_item_needs_a_known_format(string? format)
    {
        var block = Prose() with { Data = Prose().Data with { Format = format } };

        AssertIssue(Complete(block, Command(), Instructions()), "blocks[0].data.format");
    }

    // Regra 6: mínimo ≤ máximo ≤ linhas da folha do vestibular.
    [Theory]
    [InlineData(20, 30, true)]
    [InlineData(30, 30, true)]
    [InlineData(1, 1, true)]
    [InlineData(25, 20, false)]
    [InlineData(20, 31, false)]
    [InlineData(0, 30, false)]
    [InlineData(null, 30, false)]
    [InlineData(20, null, false)]
    public void Sheet_lines_fit_the_exam_sheet(int? min, int? max, bool accepted) =>
        Assert.Equal(accepted, IssuesOf(Complete() with { MinLines = min, MaxLines = max }).Count == 0);

    // Regra 7: abordagens esperadas com o recorte e os sinais de tangenciamento.
    [Fact]
    public void Grading_notes_need_expected_focus_and_tangent_signals()
    {
        AssertIssue(Complete() with { Notes = Notes with { ExpectedFocus = " " } }, "gradingNotes.expectedFocus");
        AssertIssue(Complete() with { Notes = Notes with { TangentSignals = null } }, "gradingNotes.tangentSignals");
        Assert.Empty(IssuesOf(Complete() with { Notes = Notes with { OffTopicSignals = null, KeyData = null } }));
    }

    // Regra 8: até 40 blocos, 6.000 caracteres por texto e 30.000 na proposta inteira.
    [Fact]
    public void Prompt_has_at_most_40_blocks()
    {
        PromptContent WithParagraphs(int count) => Complete(
        [
            Prose(1), Command(2), Instructions(3),
            .. Enumerable.Range(0, count).Select(i => new PromptBlockContent(null, 100 + i, PromptBlockKind.Paragraph, new PromptBlockData { Body = "Texto." })),
        ]);

        Assert.Empty(IssuesOf(WithParagraphs(37)));
        AssertRefused(() => Prompt.Draft(WithParagraphs(38), null, Now), "blocks");
    }

    [Fact]
    public void Each_text_has_at_most_6000_characters()
    {
        Assert.Empty(IssuesOf(Complete(Prose(body: new string('a', 6000)), Command(), Instructions())));
        var error = AssertRefused(
            () => Prompt.Draft(Complete(Prose(body: new string('a', 6001)), Command(), Instructions()), null, Now), "blocks[0].data");
        Assert.Contains("6.000", error.Details!.Single().Message);
    }

    [Fact]
    public void Whole_prompt_has_at_most_30000_characters()
    {
        PromptContent WithTexts(int length) => Complete(
        [
            .. Enumerable.Range(1, 5).Select(i => Prose(i, $"Texto {i}", new string('a', 5900))),
            Prose(6, "Texto 6", new string('a', length)),
            Command(60, "Redija."),
            Instructions(),
        ]) with { Title = "Tema", Summary = null };

        // 5 textos de 5.900 + rótulos e fontes já somam perto do limite; o sexto decide.
        var used = WithTexts(1).Blocks.Sum(block => PromptBlockData.For(block.Kind, block.Data).Texts().Sum(text => text.Length)) + "Tema".Length - 1;
        Assert.Empty(IssuesOf(WithTexts(30_000 - used)));
        AssertRefused(() => Prompt.Draft(WithTexts(30_000 - used + 1), null, Now), "blocks");
    }

    // Regra 9: só as marcações permitidas; qualquer < ou > é recusado.
    [Theory]
    [InlineData("Texto com <b>negrito</b>.")]
    [InlineData("a > b")]
    [InlineData("<script>alert(1)</script>")]
    public void Angle_brackets_are_refused_everywhere(string text)
    {
        AssertRefused(() => Prompt.Draft(Complete(Prose(body: text), Command(), Instructions()), null, Now), "blocks[0].data");
        AssertRefused(() => Prompt.Draft(Complete(Prose(), Command(body: text), Instructions()), null, Now), "blocks[1].data");
        AssertRefused(() => Prompt.Draft(Complete(Verse(30, [text]), Command(), Instructions()), null, Now), "blocks[0].data");
        AssertRefused(() => Prompt.Draft(Complete() with { Title = text }, null, Now), "title");
        AssertRefused(() => Prompt.Draft(Complete() with { Notes = Notes with { ExpectedFocus = text } }, null, Now), "gradingNotes.expectedFocus");
        AssertRefused(() => Prompt.Draft(Complete() with { Notes = Notes with { KeyData = [text] } }, null, Now), "gradingNotes.keyData");
    }

    [Fact]
    public void Bold_italic_and_line_breaks_are_accepted()
    {
        var prompt = Prompt.Draft(Complete(Prose(body: "Primeira linha com **negrito**\r\ne *itálico*."), Command(), Instructions()), null, Now);

        Assert.Empty(prompt.PublicationIssues(Context));
        Assert.Equal("Primeira linha com **negrito**\ne *itálico*.", prompt.Blocks[0].Data.Body);
    }

    [Fact]
    public void Block_keeps_only_the_fields_of_its_kind_and_format()
    {
        var mixed = new PromptBlockData
        {
            Label = " Texto 1 ",
            Format = CollectionFormat.Prose,
            Source = "Fonte",
            Body = "Corpo",
            Columns = [["verso"]],
            AssetId = ImageId,
            Alt = "alt",
            AiDescription = AiDescription,
            Title = "Instruções",
            Items = ["item"],
            Text = "Seção",
        };

        Assert.Equal(
            new PromptBlockData { Label = "Texto 1", Format = CollectionFormat.Prose, Source = "Fonte", Body = "Corpo" },
            PromptBlockData.For(PromptBlockKind.CollectionItem, mixed));
        Assert.Equal(new PromptBlockData { Body = "Corpo" }, PromptBlockData.For(PromptBlockKind.Command, mixed));
        Assert.Equal(new PromptBlockData { Text = "Seção" }, PromptBlockData.For(PromptBlockKind.Heading, mixed));
        Assert.Equal(PromptBlockData.None, PromptBlockData.For(PromptBlockKind.Divider, mixed));
        var image = PromptBlockData.For(PromptBlockKind.CollectionItem, mixed with { Format = CollectionFormat.Image });
        Assert.Null(image.Body);
        Assert.Equal(ImageId, image.AssetId);
    }

    [Fact]
    public void Replacing_a_draft_keeps_the_blocks_that_came_back_and_drops_the_others()
    {
        var prompt = Prompt.Draft(Complete(), null, Now);
        var command = prompt.Blocks.Single(block => block.Kind == PromptBlockKind.Command);
        var instructions = prompt.Blocks.Single(block => block.Kind == PromptBlockKind.Instructions);

        prompt.Replace(
            Complete(
                Prose(10),
                Command(20, "Enunciado novo.") with { Id = command.Id },
                Instructions(30) with { Id = instructions.Id }),
            Now.AddMinutes(5));

        Assert.Equal(3, prompt.Blocks.Count);
        Assert.Equal(command.Id, prompt.Blocks[1].Id);
        Assert.Equal("Enunciado novo.", prompt.Blocks[1].Data.Body);
        Assert.Equal(instructions.Id, prompt.Blocks[2].Id);
        Assert.Equal(Now.AddMinutes(5), prompt.UpdatedAt);
        Assert.Equal(Now, prompt.CreatedAt);
    }

    [Fact]
    public void Reordering_changes_only_positions_and_refuses_repeats()
    {
        var prompt = Prompt.Draft(Complete(Prose(10), Command(20), Instructions(30)), null, Now);
        var ids = prompt.Blocks.Select(block => block.Id).ToList();

        prompt.Reorder([new(ids[0], 20), new(ids[1], 10), new(ids[2], 30)], Now.AddMinutes(1));

        Assert.Equal([ids[1], ids[0], ids[2]], prompt.Blocks.Select(block => block.Id));
        AssertRefused(() => prompt.Reorder([new(ids[0], 10), new(ids[1], 10), new(ids[2], 30)], Now), "blocks");
        AssertRefused(() => prompt.Reorder([new(ids[0], 10), new(ids[1], 20)], Now), "blocks");
        AssertRefused(() => prompt.Reorder([new(ids[0], 10), new(ids[1], 20), new(Guid.CreateVersion7(), 30)], Now), "blocks");
    }

    [Fact]
    public void Published_prompt_is_never_edited()
    {
        var prompt = Prompt.Draft(Complete(), null, Now);
        prompt.Publish(Context, null, Now);
        var ids = prompt.Blocks.Select((block, index) => new BlockPosition(block.Id, index)).ToList();

        Assert.Equal("PROMPT_NOT_DRAFT", Assert.Throws<AppError>(() => prompt.Replace(Complete(), Now)).Code);
        Assert.Equal("PROMPT_NOT_DRAFT", Assert.Throws<AppError>(() => prompt.Reorder(ids, Now)).Code);
        Assert.Equal("PROMPT_NOT_DRAFT", Assert.Throws<AppError>(() => prompt.Publish(Context, null, Now)).Code);
    }

    [Fact]
    public void New_version_is_a_draft_with_the_same_content_and_new_blocks()
    {
        var published = Prompt.Draft(Complete(), null, Now);
        Assert.Equal("INVALID_STATE", Assert.Throws<AppError>(() => published.NewVersion(2, null, Now)).Code);
        published.Publish(Context, null, Now);
        var adminId = Guid.CreateVersion7();

        var draft = published.NewVersion(2, adminId, Now.AddDays(1));

        Assert.Equal((PromptStatus.Draft, 2, "papel-da-ciencia"), (draft.Status, draft.Version, draft.Slug));
        Assert.NotEqual(published.Id, draft.Id);
        Assert.Equal(adminId, draft.CreatedBy);
        Assert.Null(draft.PublishedAt);
        Assert.Equal(published.Blocks.Select(block => block.Data), draft.Blocks.Select(block => block.Data));
        Assert.Empty(draft.Blocks.Select(block => block.Id).Intersect(published.Blocks.Select(block => block.Id)));
        Assert.All(draft.Blocks, block => Assert.Equal(draft.Id, block.PromptId));
        Assert.Equal(published.GradingNotes.ExpectedFocus, draft.GradingNotes.ExpectedFocus);
        Assert.NotEqual(published.GradingNotes.Id, draft.GradingNotes.Id);
        Assert.Empty(draft.PublicationIssues(Context));
        // A versão publicada continua como estava.
        Assert.Equal(PromptStatus.Published, published.Status);
    }

    [Fact]
    public void Only_a_published_prompt_is_archived()
    {
        var prompt = Prompt.Draft(Complete(), null, Now);
        Assert.Equal("INVALID_STATE", Assert.Throws<AppError>(() => prompt.Archive(Now)).Code);

        prompt.Publish(Context, null, Now);
        prompt.Archive(Now.AddDays(1));

        Assert.Equal(PromptStatus.Archived, prompt.Status);
        // Arquivada, ainda serve de base para uma versão nova.
        Assert.Equal(PromptStatus.Draft, prompt.NewVersion(2, null, Now.AddDays(2)).Status);
    }
}
