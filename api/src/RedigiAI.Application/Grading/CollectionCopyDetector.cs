using System.Globalization;
using System.Text;
using RedigiAI.Domain.Catalog;
using RedigiAI.Domain.Essays;
using RedigiAI.Domain.Grading;

namespace RedigiAI.Application.Grading;

/// <summary>Limites da verificação automática de cópia da coletânea (docs/07-propostas-de-redacao.md, A6).</summary>
public sealed class CollectionCopyOptions
{
    public const string Section = "CollectionCopy";

    /// <summary>Quantas palavras iguais em sequência contam como trecho copiado.</summary>
    public int MinWords { get; set; } = 8;

    /// <summary>
    /// A partir de que parte das palavras da redação a cópia exige nota zero: se a IA não aplicar, a resposta é
    /// inconsistente e a chamada é refeita.
    /// </summary>
    public double ZeroShare { get; set; } = 0.30;
}

/// <summary>O que a verificação encontrou numa redação.</summary>
/// <param name="CopiedWords">Palavras da redação que estão em algum trecho copiado.</param>
/// <param name="TotalWords">Palavras da redação.</param>
public sealed record CollectionCopy(IReadOnlyList<CopiedPassage> Passages, int CopiedWords, int TotalWords)
{
    public double Share => TotalWords == 0 ? 0 : (double)CopiedWords / TotalWords;
}

/// <summary>
/// Procura, na redação, sequências de palavras iguais às de um texto da coletânea em prosa ou em verso. A comparação
/// ignora maiúsculas, acentos e pontuação. Imagens ficam de fora: o aluno não tem o que copiar delas.
/// </summary>
public static class CollectionCopyDetector
{
    public static CollectionCopy Find(Prompt prompt, IReadOnlyList<TranscribedLine> lines, int minWords)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minWords, 1);

        var essay = lines.SelectMany(line => Words(line.Text).Select(word => (Word: word, Line: line.Number))).ToList();
        var sources = prompt.Blocks
            .Where(block => block.Kind == PromptBlockKind.CollectionItem && block.Data.Format != CollectionFormat.Image)
            .Select(block => (Label: block.Data.Label ?? "Texto", Words: Words(TextOf(block.Data)).Select(word => word.Normalized).ToList()))
            .Where(source => source.Words.Count >= minWords)
            .ToList();

        // Onde começa, em cada texto da coletânea, cada sequência de minWords palavras.
        var starts = new Dictionary<string, List<(int Source, int Position)>>(StringComparer.Ordinal);
        for (var source = 0; source < sources.Count; source++)
        {
            var words = sources[source].Words;
            for (var position = 0; position + minWords <= words.Count; position++)
            {
                var key = string.Join(' ', words.Skip(position).Take(minWords));
                if (!starts.TryGetValue(key, out var found))
                {
                    starts[key] = found = [];
                }

                found.Add((source, position));
            }
        }

        var passages = new List<CopiedPassage>();
        var copied = 0;
        for (var index = 0; index + minWords <= essay.Count;)
        {
            var key = string.Join(' ', essay.Skip(index).Take(minWords).Select(item => item.Word.Normalized));
            if (!starts.TryGetValue(key, out var candidates))
            {
                index++;
                continue;
            }

            // O trecho vai até onde a redação e o texto da coletânea continuam iguais; vale o mais longo.
            var (best, length) = (candidates[0].Source, 0);
            foreach (var (source, position) in candidates)
            {
                var words = sources[source].Words;
                var run = minWords;
                while (index + run < essay.Count && position + run < words.Count && essay[index + run].Word.Normalized == words[position + run])
                {
                    run++;
                }

                if (run > length)
                {
                    (best, length) = (source, run);
                }
            }

            passages.Add(new CopiedPassage(
                sources[best].Label,
                essay[index].Line,
                essay[index + length - 1].Line,
                length,
                string.Join(' ', essay.Skip(index).Take(length).Select(item => item.Word.Original))));
            copied += length;
            index += length;
        }

        return new CollectionCopy(passages, copied, essay.Count);
    }

    private static string TextOf(PromptBlockData data) =>
        data.Format == CollectionFormat.Verse
            ? string.Join('\n', (data.Columns ?? []).SelectMany(column => column))
            : data.Body ?? "";

    /// <summary>As palavras do texto, como estão e na forma comparada: minúsculas, sem acento nem pontuação.</summary>
    private static IEnumerable<(string Original, string Normalized)> Words(string text)
    {
        var word = new StringBuilder();
        foreach (var character in text.Append(' '))
        {
            if (char.IsLetterOrDigit(character) || CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                word.Append(character);
                continue;
            }

            if (word.Length > 0)
            {
                var original = word.ToString();
                yield return (original, Normalize(original));
                word.Clear();
            }
        }
    }

    private static string Normalize(string word)
    {
        var normalized = new StringBuilder(word.Length);
        foreach (var character in word.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                normalized.Append(char.ToLowerInvariant(character));
            }
        }

        return normalized.ToString();
    }
}
