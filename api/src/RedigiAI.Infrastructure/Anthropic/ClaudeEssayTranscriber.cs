using System.Text.Json;
using System.Text.Json.Nodes;
using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Essays;

namespace RedigiAI.Infrastructure.Anthropic;

/// <summary>
/// Transcrição da folha pela API do Claude. A saída é sempre estruturada: a chamada define uma ferramenta com
/// JSON Schema; qualquer resposta que não seja o uso dessa ferramenta é tratada como inválida.
/// </summary>
internal sealed class ClaudeEssayTranscriber(ClaudeMessagesApi api) : IEssayTranscriber
{
    private const string ToolName = "registrar_transcricao";
    private const int MaxTokens = 4000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<TranscriptionCall> TranscribeAsync(TranscriptionRequest transcription, CancellationToken ct)
    {
        using var document = await api.SendAsync(BuildRequest(transcription), "transcription", ct);
        var root = document.RootElement;
        return new TranscriptionCall(ReadingOf(root), transcription.Model, ClaudeMessagesApi.TokensOf(root));
    }

    private static JsonObject BuildRequest(TranscriptionRequest transcription) =>
        new()
        {
            ["model"] = transcription.Model,
            ["max_tokens"] = MaxTokens,
            ["system"] = transcription.Instructions,
            ["tools"] = new JsonArray(new JsonObject
            {
                ["name"] = ToolName,
                ["description"] = "Registra a transcrição fiel da folha de redação, linha por linha.",
                ["input_schema"] = Schema(transcription.SheetLines),
            }),
            // Os modelos atuais não aceitam forçar a ferramenta; a instrução exige o uso e a resposta sem ela é recusada.
            ["tool_choice"] = new JsonObject { ["type"] = "auto" },
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(
                    new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject
                        {
                            ["type"] = "base64",
                            ["media_type"] = "image/jpeg",
                            ["data"] = Convert.ToBase64String(transcription.Jpeg),
                        },
                    },
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = $"Transcreva a redação desta folha. A folha tem {transcription.SheetLines} linhas numeradas.",
                    }),
            }),
        };

    private static JsonObject Schema(int sheetLines) =>
        new()
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("lines", "identification_found", "readable"),
            ["properties"] = new JsonObject
            {
                ["lines"] = new JsonObject
                {
                    ["type"] = "array",
                    ["maxItems"] = sheetLines,
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["required"] = new JsonArray("number", "text", "indent", "illegible"),
                        ["properties"] = new JsonObject
                        {
                            ["number"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = sheetLines },
                            ["text"] = new JsonObject { ["type"] = "string" },
                            ["indent"] = new JsonObject { ["type"] = "boolean" },
                            ["illegible"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["items"] = new JsonObject
                                {
                                    ["type"] = "object",
                                    ["additionalProperties"] = false,
                                    ["required"] = new JsonArray("start", "end"),
                                    ["properties"] = new JsonObject
                                    {
                                        ["start"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
                                        ["end"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
                                    },
                                },
                            },
                        },
                    },
                },
                ["identification_found"] = new JsonObject { ["type"] = "boolean" },
                ["readable"] = new JsonObject { ["type"] = "boolean" },
            },
        };

    /// <returns><c>null</c> quando a resposta não traz o uso da ferramenta no formato combinado.</returns>
    private static TranscriptionReading? ReadingOf(JsonElement root)
    {
        if (ClaudeMessagesApi.ToolInputOf(root, ToolName) is not { } input)
        {
            return null;
        }

        try
        {
            var output = input.Deserialize<ToolOutput>(Json);
            if (output?.Lines is null || output.Readable is null || output.IdentificationFound is null
                || output.Lines.Any(line => line.Number is null || line.Text is null))
            {
                return null;
            }

            return new TranscriptionReading(
                [.. output.Lines.Select(line => new ReadLine(line.Number!.Value, line.Text!, line.Indent ?? false))],
                output.IdentificationFound.Value,
                output.Readable.Value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class ToolOutput
    {
        public List<ToolLine>? Lines { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("identification_found")]
        public bool? IdentificationFound { get; init; }

        public bool? Readable { get; init; }
    }

    // O campo "illegible" da ferramenta é ignorado: as palavras ilegíveis são localizadas pelo marcador [?].
    private sealed record ToolLine(int? Number, string? Text, bool? Indent);
}
