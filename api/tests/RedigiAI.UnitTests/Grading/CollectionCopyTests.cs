using RedigiAI.Application.Grading;
using RedigiAI.Domain.Catalog;
using RedigiAI.Domain.Essays;

namespace RedigiAI.UnitTests.Grading;

/// <summary>Verificação automática de cópia da coletânea (docs/07-propostas-de-redacao.md, A6).</summary>
public class CollectionCopyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 15, 0, 0, TimeSpan.Zero);

    private const string Sagan =
        "Nós criamos uma civilização global em que os elementos mais cruciais dependem profundamente da ciência e da tecnologia. "
        + "Também criamos uma ordem em que quase ninguém compreende a ciência e a tecnologia. É uma receita para o desastre.";

    private static readonly Prompt Proposal = Prompt.Draft(
        new PromptContent(
            Guid.CreateVersion7(),
            "papel-da-ciencia",
            "O papel da ciência",
            null,
            null,
            Guid.CreateVersion7(),
            "Dissertação em prosa",
            20,
            30,
            TitleRequired: true,
            [
                new(null, 10, PromptBlockKind.CollectionItem, new PromptBlockData
                {
                    Label = "Texto 1",
                    Format = CollectionFormat.Image,
                    Source = "Verissimo.",
                    AssetId = Guid.CreateVersion7(),
                    Alt = "Tirinha.",
                    AiDescription = "Duas cobras numa ladeira conversam sobre quem inventou a roda, e a roda desce e atropela uma delas.",
                }),
                new(null, 20, PromptBlockKind.CollectionItem, new PromptBlockData
                {
                    Label = "Texto 3",
                    Format = CollectionFormat.Verse,
                    Source = "Gilberto Gil, *Quanta*.",
                    Columns = [["Sei que a arte é irmã da ciência", "Ambas filhas de um Deus fugaz"], ["Que faz num momento", "E no mesmo momento desfaz"]],
                }),
                new(null, 30, PromptBlockKind.CollectionItem, new PromptBlockData { Label = "Texto 4", Format = CollectionFormat.Prose, Source = "Carl Sagan, 1996.", Body = Sagan }),
                new(null, 40, PromptBlockKind.Command, new PromptBlockData
                {
                    Body = "Considerando as ideias apresentadas nos textos e também outras informações que julgar pertinentes, redija uma dissertação em prosa.",
                }),
                new(null, 50, PromptBlockKind.Instructions, new PromptBlockData { Title = "Instruções" }),
            ],
            new PromptNotesContent("Recorte.", "Tangente.", null, null, null)),
        createdBy: null,
        Now);

    private static CollectionCopy Find(params string[] lines) =>
        CollectionCopyDetector.Find(Proposal, [.. lines.Select((text, index) => new TranscribedLine(index + 1, text, false))], minWords: 8);

    [Fact]
    public void Copied_paragraph_is_found_with_its_lines_and_source()
    {
        var copy = Find(
            "A ciência está em toda parte, e pouca gente pensa nisso.",
            "Nós criamos uma civilização global em que os elementos",
            "mais cruciais dependem profundamente da ciência e da tecnologia.",
            "Por isso defendo que a escola ensine o método científico.");

        var passage = Assert.Single(copy.Passages);
        Assert.Equal(("Texto 4", 2, 3, 18), (passage.Label, passage.LineStart, passage.LineEnd, passage.Words));
        Assert.StartsWith("Nós criamos uma civilização global", passage.Text);
        Assert.EndsWith("da ciência e da tecnologia", passage.Text);
        Assert.Equal((18, 39), (copy.CopiedWords, copy.TotalWords));
        Assert.Equal(18d / 39, copy.Share, precision: 6);
    }

    [Fact]
    public void Comparison_ignores_case_accents_and_punctuation()
    {
        var copy = Find("NOS criamos, uma civilizacao GLOBAL - em que os elementos mais cruciais... dependem!");

        Assert.Equal(12, Assert.Single(copy.Passages).Words);
        // O trecho guardado é o que o aluno escreveu, não o texto da coletânea.
        Assert.StartsWith("NOS criamos uma civilizacao GLOBAL", copy.Passages[0].Text);
    }

    [Theory]
    [InlineData("criamos uma civilização global em que os elementos", 1)] // 8 palavras
    [InlineData("criamos uma civilização global em que os", 0)] // 7 palavras: não chega
    public void Passage_needs_at_least_8_equal_words_in_a_row(string line, int passages) =>
        Assert.Equal(passages, Find(line).Passages.Count);

    [Fact]
    public void Same_words_in_another_order_or_with_a_word_changed_are_not_a_copy()
    {
        Assert.Empty(Find("Nós criamos uma sociedade global em que os elementos mais importantes dependem").Passages);
        Assert.Empty(Find("A tecnologia e a ciência, quase ninguém compreende, nessa ordem que criamos também").Passages);
    }

    [Fact]
    public void Verse_is_compared_across_verses_and_columns()
    {
        var copy = Find("Como diz a canção, sei que a arte é irmã da ciência, ambas filhas de um Deus fugaz que faz num momento.");

        var passage = Assert.Single(copy.Passages);
        Assert.Equal(("Texto 3", 18), (passage.Label, passage.Words));
    }

    [Fact]
    public void Image_description_and_command_are_not_sources()
    {
        // A descrição da imagem é feita para a IA e o comando não é coletânea: repetir um deles não é cópia de texto.
        Assert.Empty(Find("Duas cobras numa ladeira conversam sobre quem inventou a roda, e a roda desce.").Passages);
        Assert.Empty(Find("Considerando as ideias apresentadas nos textos e também outras informações que julgar pertinentes.").Passages);
    }

    [Fact]
    public void Two_passages_are_counted_separately()
    {
        var copy = Find(
            "Também criamos uma ordem em que quase ninguém compreende a ciência.",
            "Eu penso diferente disso tudo.",
            "Sei que a arte é irmã da ciência, ambas filhas.");

        Assert.Equal(["Texto 4", "Texto 3"], copy.Passages.Select(passage => passage.Label));
        Assert.Equal([1, 3], copy.Passages.Select(passage => passage.LineStart));
        Assert.Equal(copy.Passages.Sum(passage => passage.Words), copy.CopiedWords);
    }

    [Fact]
    public void Essay_without_text_has_no_copy_and_no_share()
    {
        var copy = Find("", "   ");

        Assert.Empty(copy.Passages);
        Assert.Equal((0, 0, 0d), (copy.CopiedWords, copy.TotalWords, copy.Share));
    }

    [Fact]
    public void Whole_essay_copied_has_share_one()
    {
        var copy = Find(Sagan);

        Assert.Equal(1d, copy.Share);
        Assert.Single(copy.Passages);
    }
}
