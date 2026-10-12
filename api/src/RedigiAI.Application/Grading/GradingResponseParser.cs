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
            || string.IsNullOrWhiteSpace(response.MainIssue)
            || response.NextSteps.Any(string.IsNullOrWhiteSpace))
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

            criteria.Add(new GradedCriterion(position, level, item.Justification, item.Lines, item.Guidance));
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

            marks.Add(new GradedMark(criterion, kind, line, start, end, item.Note, item.Rule));
        }

        return new GradingOutput(
            new GradedZero(applies, response.Zero.Reason, response.Zero.Explanation),
            criteria,
            marks,
            response.Strengths,
            response.MainIssue,
            response.NextSteps!);
    }

    private sealed class Response
    {
        public Zero? Zero { get; init; }
        public List<Criterion?>? Criteria { get; init; }
        public List<Mark?>? Marks { get; init; }
        public string? Strengths { get; init; }
        public string? MainIssue { get; init; }
        public List<string>? NextSteps { get; init; }
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
    }
}
