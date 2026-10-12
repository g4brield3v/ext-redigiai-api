using RedigiAI.Domain.Common;

namespace RedigiAI.Domain.Essays;

/// <param name="Indent">A linha começa com recuo de parágrafo.</param>
public sealed record TranscribedLine(int Number, string Text, bool Indent);

/// <summary>Uma linha como a IA leu, com os trechos que ela não conseguiu ler.</summary>
/// <param name="Illegible">Posições no texto da linha: início incluído, fim excluído.</param>
public sealed record ReadLine(int Number, string Text, bool Indent, IReadOnlyList<TextRange> Illegible);

public readonly record struct TextRange(int Start, int End);

/// <summary>A leitura da IA não respeita o formato combinado; vale pedir de novo.</summary>
public sealed class InvalidTranscriptionException(string reason) : Exception(reason);

/// <summary>
/// Cópia fiel do que está escrito na folha, linha por linha. Nunca é editada: o que o aluno digita para as
/// palavras ilegíveis fica separado, em <see cref="IllegibleWord"/>.
/// </summary>
public sealed class Transcription
{
    /// <summary>Acima disso a foto é considerada ilegível (regra 7).</summary>
    public const int MaxIllegibleWords = 10;

    /// <summary>Uma linha manuscrita não chega perto disso; protege contra respostas absurdas.</summary>
    public const int MaxLineLength = 400;

    /// <summary>O que aparece no texto no lugar de cada palavra ilegível.</summary>
    public const string IllegibleMarker = "[?]";

    private readonly List<IllegibleWord> _words = [];

    private Transcription() { }

    public Guid Id { get; private set; }
    public Guid EssayId { get; private set; }
    public IReadOnlyList<TranscribedLine> Lines { get; private set; } = [];
    public int IllegibleCount { get; private set; }

    /// <summary>Há nome, assinatura ou outra identificação na folha (hipótese de nota zero).</summary>
    public bool IdentificationFound { get; private set; }
    public string Model { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
    public IReadOnlyList<IllegibleWord> Words => _words;

    public bool IsUnreadable => IllegibleCount > MaxIllegibleWords;

    /// <exception cref="InvalidTranscriptionException">Linha fora da folha, repetida ou trecho fora do texto.</exception>
    public static Transcription Create(
        Guid essayId,
        int sheetLines,
        IReadOnlyList<ReadLine> read,
        bool identificationFound,
        string model,
        DateTimeOffset now)
    {
        var transcription = new Transcription
        {
            Id = Guid.CreateVersion7(),
            EssayId = essayId,
            IdentificationFound = identificationFound,
            Model = model,
            CreatedAt = now,
        };

        var seen = new HashSet<int>();
        var lines = new List<TranscribedLine>(read.Count);
        foreach (var line in read.OrderBy(l => l.Number))
        {
            if (line.Number < 1 || line.Number > sheetLines || !seen.Add(line.Number))
            {
                throw new InvalidTranscriptionException($"Line {line.Number} is outside the sheet or repeated.");
            }

            if (line.Text.Length > MaxLineLength || line.Text.Any(char.IsControl))
            {
                throw new InvalidTranscriptionException($"Line {line.Number} has an invalid text.");
            }

            foreach (var range in RangesOf(line))
            {
                transcription._words.Add(IllegibleWord.Create(transcription.Id, line.Number, range));
            }

            lines.Add(new TranscribedLine(line.Number, line.Text, line.Indent));
        }

        transcription.Lines = lines;
        transcription.IllegibleCount = transcription._words.Count;
        return transcription;
    }

    /// <summary>
    /// Os marcadores <c>[?]</c> no texto dizem onde estão as palavras ilegíveis; modelos de linguagem erram
    /// contagem de caracteres, então a posição informada só vale quando a linha não tem marcador.
    /// </summary>
    private static List<TextRange> RangesOf(ReadLine line)
    {
        var ranges = new List<TextRange>();
        for (var at = line.Text.IndexOf(IllegibleMarker, StringComparison.Ordinal);
             at >= 0;
             at = line.Text.IndexOf(IllegibleMarker, at + IllegibleMarker.Length, StringComparison.Ordinal))
        {
            ranges.Add(new TextRange(at, at + IllegibleMarker.Length));
        }

        if (ranges.Count > 0)
        {
            return ranges;
        }

        var previousEnd = 0;
        foreach (var range in line.Illegible.OrderBy(r => r.Start))
        {
            if (range.Start < previousEnd || range.End <= range.Start || range.End > line.Text.Length)
            {
                throw new InvalidTranscriptionException($"Line {line.Number} has an illegible range outside its text.");
            }

            ranges.Add(range);
            previousEnd = range.End;
        }

        return ranges;
    }
}

/// <summary>Palavra que a IA não conseguiu ler e que o aluno digita na revisão.</summary>
public sealed class IllegibleWord
{
    public const int MaxValueLength = 60;

    private IllegibleWord() { }

    public Guid Id { get; private set; }
    public Guid TranscriptionId { get; private set; }
    public int LineNumber { get; private set; }
    public int CharStart { get; private set; }
    public int CharEnd { get; private set; }
    public string? StudentValue { get; private set; }
    public DateTimeOffset? FilledAt { get; private set; }

    internal static IllegibleWord Create(Guid transcriptionId, int lineNumber, TextRange range) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TranscriptionId = transcriptionId,
            LineNumber = lineNumber,
            CharStart = range.Start,
            CharEnd = range.End,
        };

    /// <summary>Guarda o que o aluno digitou, como ele digitou: nada aqui é corrigido.</summary>
    public void Fill(string? value, DateTimeOffset now)
    {
        var typed = value?.Trim() ?? "";
        if (typed.Length == 0)
        {
            throw Errors.ReviewIncomplete();
        }

        if (typed.Length > MaxValueLength || typed.Any(char.IsControl))
        {
            throw Errors.Validation($"Cada palavra pode ter até {MaxValueLength} caracteres.");
        }

        StudentValue = typed;
        FilledAt = now;
    }
}

/// <summary>Consumo de uma chamada à IA, para o custo por redação e o teto de gasto.</summary>
public sealed class AiUsage
{
    public const string Transcription = "transcription";

    private AiUsage() { }

    public Guid Id { get; private set; }
    public Guid EssayId { get; private set; }
    public string Stage { get; private set; } = null!;
    public string Model { get; private set; } = null!;
    public int InputTokens { get; private set; }
    public int OutputTokens { get; private set; }
    public int CacheReadTokens { get; private set; }
    public int CacheWriteTokens { get; private set; }

    /// <summary>Em milionésimos de dólar; nulo quando o preço do modelo não está configurado.</summary>
    public long? CostMicroUsd { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static AiUsage Record(
        Guid essayId,
        string stage,
        string model,
        int inputTokens,
        int outputTokens,
        int cacheReadTokens,
        int cacheWriteTokens,
        long? costMicroUsd,
        DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            EssayId = essayId,
            Stage = stage,
            Model = model,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CacheReadTokens = cacheReadTokens,
            CacheWriteTokens = cacheWriteTokens,
            CostMicroUsd = costMicroUsd,
            CreatedAt = now,
        };
}
