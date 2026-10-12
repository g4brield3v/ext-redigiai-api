using RedigiAI.Domain.Catalog;

namespace RedigiAI.Application.Grading;

/// <summary>
/// A proposta de redação convertida em texto para a IA, na ordem dos blocos (docs/07-propostas-de-redacao.md, A6).
/// A correção só recebe texto: de uma imagem vai a descrição cadastrada. Títulos de seção e separadores não vão.
/// </summary>
internal static class PromptText
{
    /// <summary>
    /// Os blocos na ordem da página: <c>[Texto 2] corpo — Fonte</c>, <c>[Comando] corpo</c>,
    /// <c>[Instruções] Tipo de redação: …; item; item</c>.
    /// </summary>
    /// <param name="genre">O tipo de redação, que abre as instruções.</param>
    public static string Blocks(Prompt prompt, string genre) =>
        string.Join("\n", prompt.Blocks
            .Select(block => block.Kind == PromptBlockKind.CollectionItem ? CollectionItem(block.Data) : Statement(block, genre))
            .OfType<string>());

    /// <summary>Texto que vai dentro de um atributo da marcação: sem aspas duplas, que fechariam o atributo.</summary>
    public static string Attribute(string text) => text.Replace('"', '\'');

    /// <summary>Os trechos copiados da coletânea, um por linha, ou "nenhum".</summary>
    public static string CopiedPassages(IReadOnlyList<Domain.Grading.CopiedPassage> passages) =>
        passages.Count == 0
            ? "nenhum"
            : "\n" + string.Join("\n", passages.Select(passage =>
                $"{Lines(passage)}, igual ao {passage.Label}, {passage.Words} palavras: {GradingPromptBuilder.AsData(passage.Text)}")) + "\n";

    private static string Lines(Domain.Grading.CopiedPassage passage) =>
        passage.LineStart == passage.LineEnd ? $"Linha {passage.LineStart}" : $"Linhas {passage.LineStart} a {passage.LineEnd}";

    /// <summary>As abordagens esperadas do tema.</summary>
    public static string GradingNotes(PromptGradingNotes notes)
    {
        var lines = new List<string>
        {
            $"Recorte esperado: {notes.ExpectedFocus}",
            $"Sinais de tangenciamento: {notes.TangentSignals}",
        };
        if (notes.OffTopicSignals.Length > 0)
        {
            lines.Add($"Sinais de fuga ao tema: {notes.OffTopicSignals}");
        }

        if (notes.KeyData.Count > 0)
        {
            lines.Add($"Dados-chave da coletânea: {string.Join("; ", notes.KeyData)}");
        }

        if (notes.Extra.Length > 0)
        {
            lines.Add(notes.Extra);
        }

        return string.Join("\n", lines);
    }

    /// <summary>O tipo exatamente como a prova pede; é contra ele que a modalidade é avaliada.</summary>
    public static string Genre(Prompt prompt, EssayType essayType) => prompt.GenreLabel ?? essayType.Name;

    private static string CollectionItem(PromptBlockData data)
    {
        var (mark, content) = data.Format switch
        {
            CollectionFormat.Verse => (" · verso", string.Join(" / ", (data.Columns ?? []).Select(column => string.Join("\n", column)))),
            CollectionFormat.Image => (" · imagem", data.AiDescription ?? ""),
            _ => ("", data.Body ?? ""),
        };
        return $"[{data.Label}{mark}] {content} — {data.Source}";
    }

    private static string? Statement(PromptBlock block, string genre) => block.Kind switch
    {
        PromptBlockKind.Command => $"[Comando] {block.Data.Body}",
        PromptBlockKind.Paragraph => block.Data.Body,
        PromptBlockKind.Instructions => "[Instruções] " + string.Join("; ", (block.Data.Items ?? []).Prepend($"Tipo de redação: {genre}")),
        _ => null,
    };
}
