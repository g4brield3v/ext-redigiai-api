using System.Text.Json;
using RedigiAI.Domain.Essays;

namespace RedigiAI.Application.Essays;

/// <summary>
/// Monta a linha do tempo da redação a partir da trilha de auditoria. Só entram os eventos e os campos da lista
/// abaixo: o resto da trilha (tentativas da IA, custo, códigos internos) nunca chega ao aluno.
/// </summary>
internal static class EssayTimeline
{
    public static IReadOnlyList<EssayEventDto> Build(Essay essay, IReadOnlyList<EssayHistoryEntry> history)
    {
        var events = new List<EssayEventDto>(history.Count);
        foreach (var entry in history)
        {
            if (ToEvent(essay, entry) is { } item)
            {
                events.Add(item);
            }
        }

        return events;
    }

    private static EssayEventDto? ToEvent(Essay essay, EssayHistoryEntry entry)
    {
        switch (entry.Action)
        {
            case "essay.submitted":
                return new EssayEventDto(entry.At, EssayEventType.Submitted, null);
            case "credit.refunded":
                return new EssayEventDto(entry.At, EssayEventType.CreditRefunded, null);
            case "essay.status_changed":
                break;
            default:
                return null;
        }

        using var data = Parse(entry.Data);
        var root = data?.RootElement;
        var from = Status(root, "from");
        var to = Status(root, "to");

        // O código vem da redação, que só guarda os códigos públicos; o da trilha não é repassado.
        return (from, to) switch
        {
            (_, EssayStatus.Failed) => new EssayEventDto(entry.At, EssayEventType.Failed, FailureDetails(essay)),
            (_, EssayStatus.Cancelled) => new EssayEventDto(entry.At, EssayEventType.Cancelled, FailureDetails(essay)),
            (EssayStatus.Uploaded, EssayStatus.Queued or EssayStatus.AwaitingReview) => new EssayEventDto(
                entry.At, EssayEventType.Transcribed, new EssayEventDetailsDto(Count(root, "illegible") ?? 0, null)),
            (EssayStatus.AwaitingReview, EssayStatus.Queued) => new EssayEventDto(entry.At, EssayEventType.ReviewCompleted, null),
            (_, EssayStatus.Processing) => new EssayEventDto(entry.At, EssayEventType.GradingStarted, null),
            (_, EssayStatus.Completed) => new EssayEventDto(entry.At, EssayEventType.Completed, null),
            _ => null,
        };
    }

    /// <summary>Detalhes do motivo, os mesmos de <c>failure.details</c>, com o código.</summary>
    public static EssayEventDetailsDto? FailureDetails(Essay essay) =>
        essay.FailureCode is null ? null : new EssayEventDetailsDto(essay.FailureDetails?.IllegibleCount, essay.FailureCode);

    private static JsonDocument? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static EssayStatus? Status(JsonElement? root, string name) =>
        root is { ValueKind: JsonValueKind.Object } value
        && value.TryGetProperty(name, out var field)
        && field.ValueKind == JsonValueKind.String
        && Enum.TryParse<EssayStatus>(field.GetString(), ignoreCase: false, out var status)
            ? status
            : null;

    private static int? Count(JsonElement? root, string name) =>
        root is { ValueKind: JsonValueKind.Object } value
        && value.TryGetProperty(name, out var field)
        && field.ValueKind == JsonValueKind.Number
        && field.TryGetInt32(out var count)
            ? count
            : null;
}
