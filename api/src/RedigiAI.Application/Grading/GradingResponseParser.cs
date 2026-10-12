using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using RedigiAI.Domain.Grading;

namespace RedigiAI.Application.Grading;

/// <summary>
/// Regra 1 da validação: a resposta da IA precisa ser exatamente o JSON combinado. Campo faltando, tipo errado
/// ou campo a mais recusa a resposta; é assim que uma nota enviada pelo modelo nunca chega a ser usada.
/// </summary>
internal static class GradingResponseParser
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <returns><c>null</c> quando a resposta está fora do formato.</returns>
    public static GradingOutput? Parse(string? toolInput)
    {
        if (string.IsNullOrWhiteSpace(toolInput))
        {
            return null;
        }

        Response? response;
        try
        {
            response = JsonSerializer.Deserialize<Response>(toolInput, Json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (response?.Zero?.Applies is not { } applies
            || response.Criteria is null
            || response.Marks is null
            || response.NextSteps is null
            || string.IsNullOrWhiteSpace(response.Strengths)
            || string.IsNullOrWhiteSpace(response.MainIssue?.Text)
            || response.NextSteps.Any(step => string.IsNullOrWhiteSpace(step?.Text)))
        {
            return null;
        }

        var criteria = new List<GradedCriterion>(response.Criteria.Count);
        foreach (var item in response.Criteria)
        {
            if (item?.Position is not { } position || item.Level is not { } level
                || string.IsNullOrWhiteSpace(item.Justification) || item.Lines is null)
            {
                return null;
            }

            criteria.Add(new GradedCriterion(position, level, Unescaped(item.Justification), item.Lines, Unescaped(item.Guidance)));
        }

        var marks = new List<GradedMark>(response.Marks.Count);
        foreach (var item in response.Marks)
        {
            MarkKind kind;
            switch (item?.Kind)
            {
                case "good":
                    kind = MarkKind.Good;
                    break;
                case "issue":
                    kind = MarkKind.Issue;
                    break;
                default:
                    return null;
            }

            if (item.Criterion is not { } criterion || item.Line is not { } line || item.Start is not { } start || item.End is not { } end)
            {
                return null;
            }

            marks.Add(new GradedMark(criterion, kind, line, start, end, Unescaped(item.Note), item.Rule, Unescaped(item.Fix)));
        }

        return new GradingOutput(
            new GradedZero(applies, response.Zero.Reason, Unescaped(response.Zero.Explanation)),
            criteria,
            marks,
            Unescaped(response.Strengths),
            new GradedPoint(Unescaped(response.MainIssue.Text), response.MainIssue.Criterion),
            [.. response.NextSteps.Select(step => new GradedPoint(Unescaped(step!.Text!), step.Criterion))]);
    }

    /// <summary>
    /// O modelo às vezes escreve as aspas já com a barra de escape (<c>\"A vida é bela\"</c>) dentro do texto.
    /// A barra sai aqui, antes de qualquer validação; a resposta como veio continua em <c>raw_response</c>.
    /// </summary>
    [return: NotNullIfNotNull(nameof(text))]
    internal static string? Unescaped(string? text) => text?.Replace("\\\"", "\"");

    private sealed class Response
    {
        public Zero? Zero { get; init; }
        public List<Criterion?>? Criteria { get; init; }
        public List<Mark?>? Marks { get; init; }
        public string? Strengths { get; init; }
        public Point? MainIssue { get; init; }
        public List<Point?>? NextSteps { get; init; }
    }

    private sealed class Point
    {
        public string? Text { get; init; }
        public int? Criterion { get; init; }
    }

    private sealed class Zero
    {
        public bool? Applies { get; init; }
        public string? Reason { get; init; }
        public string? Explanation { get; init; }
    }

    private sealed class Criterion
    {
        public int? Position { get; init; }
        public int? Level { get; init; }
        public string? Justification { get; init; }
        public List<int>? Lines { get; init; }
        public string? Guidance { get; init; }
    }

    private sealed class Mark
    {
        public int? Criterion { get; init; }
        public string? Kind { get; init; }
        public int? Line { get; init; }
        public int? Start { get; init; }
        public int? End { get; init; }
        public string? Note { get; init; }
        public string? Rule { get; init; }
        public string? Fix { get; init; }
    }
}
