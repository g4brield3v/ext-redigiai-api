using RedigiAI.Domain.Catalog;

namespace RedigiAI.Application.Grading;

/// <summary>
/// A proposta de redação convertida em texto para a IA, na ordem dos blocos (docs/07-propostas-de-redacao.md, A6).
/// A correção só recebe texto: de uma imagem vai a descrição cadastrada. Títulos de seção e separadores não vão.
/// </summary>
internal static class PromptText
{
    /// <summary>Os textos da coletânea, um por linha: <c>[Texto 2] corpo — Fonte</c>.</summary>
    public static string Collection(Prompt prompt) =>
        string.Join("\n", prompt.Blocks.Where(block => block.Kind == PromptBlockKind.CollectionItem).Select(block => CollectionItem(block.Data)));

    /// <summary>O comando, os parágrafos livres e as instruções, com o tipo de redação como primeiro item.</summary>
    public static string Statement(Prompt prompt) =>
        string.Join("\n", prompt.Blocks.Select(block => Statement(prompt, block)).OfType<string>());

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

    private static string? Statement(Prompt prompt, PromptBlock block) => block.Kind switch
    {
        PromptBlockKind.Command => $"[Comando] {block.Data.Body}",
        PromptBlockKind.Paragraph => block.Data.Body,
        PromptBlockKind.Instructions =>
            $"[{block.Data.Title ?? "Instruções"}] " + string.Join("; ", (block.Data.Items ?? []).Prepend($"Tipo de redação: {prompt.GenreLabel}")),
        _ => null,
    };
}
