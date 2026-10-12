namespace RedigiAI.Domain.Catalog;

/// <summary>Tipos de bloco da página da proposta (docs/07-propostas-de-redacao.md, A3).</summary>
public enum PromptBlockKind
{
    /// <summary>Um texto da coletânea ("Texto 1"), em prosa, verso ou imagem.</summary>
    CollectionItem,

    /// <summary>O enunciado.</summary>
    Command,

    /// <summary>As instruções; exatamente um por proposta. O tipo de redação é o primeiro item, gerado na exibição.</summary>
    Instructions,

    /// <summary>Título de seção livre. A IA não recebe.</summary>
    Heading,
    Paragraph,

    /// <summary>Linha separadora. A IA não recebe.</summary>
    Divider,
}

/// <summary>Formato de um texto da coletânea, como vai gravado em <see cref="PromptBlockData.Format"/>.</summary>
public static class CollectionFormat
{
    public const string Prose = "prose";
    public const string Verse = "verse";
    public const string Image = "image";

    public static bool IsKnown(string? format) => format is Prose or Verse or Image;
}

/// <summary>
/// Conteúdo de um bloco (<c>data</c>, em jsonb). Cada tipo usa só os seus campos; os textos aceitam um markdown
/// mínimo (<c>**negrito**</c>, <c>*itálico*</c> e quebra de linha), nunca HTML.
/// </summary>
public sealed record PromptBlockData
{
    public static PromptBlockData None { get; } = new();

    /// <summary>Rótulo do texto da coletânea ("Texto 1").</summary>
    public string? Label { get; init; }

    /// <summary><c>prose</c>, <c>verse</c> ou <c>image</c>.</summary>
    public string? Format { get; init; }
    public string? Source { get; init; }

    /// <summary>Texto em prosa da coletânea, enunciado ou parágrafo livre.</summary>
    public string? Body { get; init; }

    /// <summary>Verso: as colunas, cada uma com os seus versos.</summary>
    public IReadOnlyList<IReadOnlyList<string>>? Columns { get; init; }
    public Guid? AssetId { get; init; }

    /// <summary>Texto alternativo da imagem, para leitores de tela.</summary>
    public string? Alt { get; init; }

    /// <summary>O que a imagem mostra, com as falas transcritas: a correção só recebe texto.</summary>
    public string? AiDescription { get; init; }

    /// <summary>Título da seção de instruções.</summary>
    public string? Title { get; init; }

    /// <summary>Itens das instruções, um por linha.</summary>
    public IReadOnlyList<string>? Items { get; init; }

    /// <summary>Texto do título de seção livre.</summary>
    public string? Text { get; init; }

    /// <summary>Só os campos do tipo (e, na coletânea, do formato): o que não pertence ao bloco não é guardado.</summary>
    public static PromptBlockData For(PromptBlockKind kind, PromptBlockData data) => kind switch
    {
        PromptBlockKind.CollectionItem => data.Format switch
        {
            CollectionFormat.Verse => new PromptBlockData
            {
                Label = Clean(data.Label),
                Format = data.Format,
                Source = Clean(data.Source),
                Columns = [.. (data.Columns ?? []).Select(column => (IReadOnlyList<string>)[.. (column ?? []).Select(verse => verse ?? "")])],
            },
            CollectionFormat.Image => new PromptBlockData
            {
                Label = Clean(data.Label),
                Format = data.Format,
                Source = Clean(data.Source),
                AssetId = data.AssetId,
                Alt = Clean(data.Alt),
                AiDescription = Clean(data.AiDescription),
            },
            _ => new PromptBlockData { Label = Clean(data.Label), Format = data.Format, Source = Clean(data.Source), Body = Clean(data.Body) },
        },
        PromptBlockKind.Command or PromptBlockKind.Paragraph => new PromptBlockData { Body = Clean(data.Body) },
        PromptBlockKind.Instructions => new PromptBlockData
        {
            Title = Clean(data.Title),
            Items = [.. (data.Items ?? []).Select(Clean).OfType<string>()],
        },
        PromptBlockKind.Heading => new PromptBlockData { Text = Clean(data.Text) },
        _ => None,
    };

    /// <summary>Todos os textos do bloco, para os limites de tamanho e a conferência das marcações.</summary>
    public IEnumerable<string> Texts()
    {
        string?[] single = [Label, Source, Body, Alt, AiDescription, Title, Text];
        return single.OfType<string>()
            .Concat(Items ?? [])
            .Concat((Columns ?? []).SelectMany(column => column ?? []))
            .Where(text => text is not null);
    }

    private static string? Clean(string? text) => PromptRules.Clean(text);
}

public sealed class PromptBlock
{
    private PromptBlock() { }

    public Guid Id { get; private set; }
    public Guid PromptId { get; private set; }

    /// <summary>Ordem na página, de cima para baixo. Única na proposta; de 10 em 10 para poder inserir no meio.</summary>
    public int Position { get; private set; }
    public PromptBlockKind Kind { get; private set; }
    public PromptBlockData Data { get; private set; } = PromptBlockData.None;

    internal static PromptBlock Create(Guid promptId, int position, PromptBlockKind kind, PromptBlockData data) =>
        new() { Id = Guid.CreateVersion7(), PromptId = promptId, Position = position, Kind = kind, Data = data };

    internal void Change(int position, PromptBlockKind kind, PromptBlockData data)
    {
        Position = position;
        Kind = kind;
        Data = data;
    }

    internal void MoveTo(int position) => Position = position;
}
