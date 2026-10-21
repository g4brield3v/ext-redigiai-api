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

    /// <summary>Um card com exatamente 3 itens empilhados, cada um texto ou imagem.</summary>
    Card,
}

/// <summary>Formato de um item do card, como vai gravado em <see cref="PromptCardItem.Format"/>.</summary>
public static class CardItemFormat
{
    public const string Text = "text";
    public const string Image = "image";

    public static bool IsKnown(string? format) => format is Text or Image;
}

/// <summary>Um dos 3 itens de um card: texto (<see cref="Body"/>) ou imagem (<see cref="AssetId"/>).</summary>
public sealed record PromptCardItem
{
    /// <summary><c>text</c> ou <c>image</c>.</summary>
    public string? Format { get; init; }
    public string? Body { get; init; }
    public Guid? AssetId { get; init; }

    /// <summary>Texto alternativo da imagem, para leitores de tela.</summary>
    public string? Alt { get; init; }

    /// <summary>O que a imagem mostra, com as falas transcritas: a correção só recebe texto.</summary>
    public string? AiDescription { get; init; }

    /// <summary>Legenda ou fonte, abaixo do item. Opcional.</summary>
    public string? Caption { get; init; }

    /// <summary>Só os campos do formato.</summary>
    internal static PromptCardItem For(PromptCardItem? item) => item?.Format switch
    {
        CardItemFormat.Image => new PromptCardItem
        {
            Format = item.Format,
            AssetId = item.AssetId,
            Alt = Clean(item.Alt),
            AiDescription = Clean(item.AiDescription),
            Caption = Clean(item.Caption),
        },
        _ => new PromptCardItem { Format = item?.Format, Body = Clean(item?.Body), Caption = Clean(item?.Caption) },
    };

    internal IEnumerable<string> Texts() => new[] { Body, Alt, AiDescription, Caption }.OfType<string>();

    private static string? Clean(string? text) => PromptRules.Clean(text);
}

/// <summary>Formato de um texto da coletânea, como vai gravado em <see cref="PromptBlockData.Format"/>.</summary>
public static class CollectionFormat
{
    public const string Prose = "prose";
    public const string Verse = "verse";
    public const string Image = "image";

    public static bool IsKnown(string? format) => format is Prose or Verse or Image;
}

/// <summary>Largura de um texto da coletânea na página, como vai gravada em <see cref="PromptBlockData.Width"/>.</summary>
public static class CollectionWidth
{
    /// <summary>A largura inteira (o padrão, gravado como ausente).</summary>
    public const string Full = "full";

    /// <summary>Metade: fica ao lado do texto vizinho que também é metade; no celular, um embaixo do outro.</summary>
    public const string Half = "half";

    public static bool IsKnown(string? width) => width is Full or Half;
}

/// <summary>
/// Conteúdo de um bloco (<c>data</c>, em jsonb). Cada tipo usa só os seus campos; os textos aceitam um markdown
/// mínimo (<c>**negrito**</c>, <c>*itálico*</c> e quebra de linha), nunca HTML.
/// </summary>
public sealed record PromptBlockData
{
    public static PromptBlockData None { get; } = new();

    /// <summary>Título da seção de instruções quando o cadastro não informa outro.</summary>
    public const string DefaultInstructionsTitle = "Instruções";

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

    /// <summary>Os itens do card, na ordem em que aparecem.</summary>
    public IReadOnlyList<PromptCardItem>? CardItems { get; init; }

    /// <summary>Texto da coletânea: <c>half</c> para ficar lado a lado com o vizinho; ausente é a largura inteira.</summary>
    public string? Width { get; init; }

    /// <summary>
    /// Texto da coletânea sem o rótulo na tela, como nas provas que não numeram os textos. A correção e a verificação
    /// de cópia continuam usando o rótulo.
    /// </summary>
    public bool? HideLabel { get; init; }

    /// <summary>Notas de rodapé do texto da coletânea (glossário, tradução), entre o texto e a fonte.</summary>
    public IReadOnlyList<string>? Notes { get; init; }

    /// <summary>Só os campos do tipo (e, na coletânea, do formato): o que não pertence ao bloco não é guardado.</summary>
    public static PromptBlockData For(PromptBlockKind kind, PromptBlockData data) => kind switch
    {
        PromptBlockKind.CollectionItem => CollectionItem(data) with
        {
            Label = Clean(data.Label),
            Format = data.Format,
            Source = Clean(data.Source),
            // A largura inteira é o padrão: gravada como ausente. Uma desconhecida fica, para a validação recusar.
            Width = Clean(data.Width) is { } width && width != CollectionWidth.Full ? width : null,
            HideLabel = data.HideLabel == true ? true : null,
            Notes = (data.Notes ?? []).Select(Clean).OfType<string>().ToList() is { Count: > 0 } notes ? notes : null,
        },
        PromptBlockKind.Command or PromptBlockKind.Paragraph => new PromptBlockData { Body = Clean(data.Body) },
        PromptBlockKind.Instructions => new PromptBlockData
        {
            Title = Clean(data.Title) ?? DefaultInstructionsTitle,
            Items = [.. (data.Items ?? []).Select(Clean).OfType<string>()],
        },
        PromptBlockKind.Heading => new PromptBlockData { Text = Clean(data.Text) },
        PromptBlockKind.Card => new PromptBlockData
        {
            Title = Clean(data.Title),
            CardItems = [.. (data.CardItems ?? []).Select(PromptCardItem.For)],
        },
        _ => None,
    };

    /// <summary>O conteúdo do formato do texto da coletânea; os campos comuns a todos os formatos vêm de <see cref="For"/>.</summary>
    private static PromptBlockData CollectionItem(PromptBlockData data) => data.Format switch
    {
        CollectionFormat.Verse => new PromptBlockData
        {
            Columns = [.. (data.Columns ?? []).Select(column => (IReadOnlyList<string>)[.. (column ?? []).Select(verse => verse ?? "")])],
        },
        CollectionFormat.Image => new PromptBlockData
        {
            AssetId = data.AssetId,
            Alt = Clean(data.Alt),
            AiDescription = Clean(data.AiDescription),
        },
        _ => new PromptBlockData { Body = Clean(data.Body) },
    };

    /// <summary>Todos os textos do bloco, para os limites de tamanho e a conferência das marcações.</summary>
    public IEnumerable<string> Texts()
    {
        string?[] single = [Label, Source, Body, Alt, AiDescription, Title, Text];
        return single.OfType<string>()
            .Concat(Items ?? [])
            .Concat(Notes ?? [])
            .Concat((Columns ?? []).SelectMany(column => column ?? []))
            .Concat((CardItems ?? []).SelectMany(item => item.Texts()))
            .Where(text => text is not null);
    }

    /// <summary>As imagens do bloco: a do texto da coletânea e as dos itens do card.</summary>
    public IEnumerable<Guid> AssetIds() =>
        new[] { AssetId }.Concat((CardItems ?? []).Select(item => item.AssetId)).OfType<Guid>();

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
