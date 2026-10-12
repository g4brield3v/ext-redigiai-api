using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Common;
using RedigiAI.Domain.Essays;

namespace RedigiAI.Infrastructure.Anthropic;

public sealed class AnthropicOptions
{
    public const string Section = "Anthropic";

    public string ApiKey { get; set; } = "";

    public string BaseUrl { get; set; } = "https://api.anthropic.com";
}

/// <summary>
/// Transcrição da folha pela API do Claude. A saída é sempre estruturada: a chamada define uma ferramenta com
/// JSON Schema; qualquer resposta que não seja o uso dessa ferramenta é tratada como inválida.
/// </summary>
internal sealed class ClaudeEssayTranscriber(IOptions<AnthropicOptions> options, ILogger<ClaudeEssayTranscriber> logger)
    : IEssayTranscriber
{
    private const string ToolName = "registrar_transcricao";
    private const int MaxTokens = 4000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Um cliente para a vida do processo. O tempo limite de cada chamada é o da especificação: 3 minutos.
    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = TimeSpan.FromMinutes(3),
    };

    public async Task<TranscriptionCall> TranscribeAsync(TranscriptionRequest transcription, CancellationToken ct)
    {
        var settings = options.Value;
        if (settings.ApiKey.Length == 0)
        {
            throw new InvalidOperationException("Anthropic is not configured: set Anthropic__ApiKey.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.BaseUrl.TrimEnd('/')}/v1/messages")
        {
            Content = JsonContent.Create(BuildRequest(transcription)),
        };
        request.Headers.Add("x-api-key", settings.ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning("Claude transcription call did not complete: {Error}", ex.GetType().Name);
            throw Errors.AiUnavailable();
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw await FailureOfAsync(response, ct);
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = document.RootElement;
            return new TranscriptionCall(ReadingOf(root), transcription.Model, TokensOf(root));
        }
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
        // Resposta cortada pelo limite de tokens não é confiável.
        if (root.TryGetProperty("stop_reason", out var stop) && stop.GetString() == "max_tokens")
        {
            return null;
        }

        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var type) && type.GetString() == "tool_use"
                && block.TryGetProperty("name", out var name) && name.GetString() == ToolName
                && block.TryGetProperty("input", out var input))
            {
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
        }

        return null;
    }

    private static AiTokens TokensOf(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage))
        {
            return new AiTokens(0, 0, 0, 0);
        }

        int Read(string name) => usage.TryGetProperty(name, out var value) && value.TryGetInt32(out var count) ? count : 0;
        return new AiTokens(
            Read("input_tokens"), Read("output_tokens"), Read("cache_read_input_tokens"), Read("cache_creation_input_tokens"));
    }

    /// <summary>429, 5xx e sobrecarga valem nova tentativa; o resto (pedido recusado, chave inválida) é definitivo.</summary>
    private async Task<AppError> FailureOfAsync(HttpResponseMessage response, CancellationToken ct)
    {
        // O erro da API descreve o pedido (modelo, formato, saldo), não o texto do aluno.
        string? errorType = null;
        string? errorMessage = null;
        try
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (body.RootElement.TryGetProperty("error", out var error))
            {
                errorType = error.TryGetProperty("type", out var type) ? type.GetString() : null;
                errorMessage = error.TryGetProperty("message", out var message) ? message.GetString() : null;
            }
        }
        catch (JsonException)
        {
            // Corpo que não é JSON: fica só o status.
        }

        var status = (int)response.StatusCode;
        logger.LogWarning(
            "Claude transcription call failed with {Status} ({ErrorType}): {ErrorMessage}",
            status,
            errorType,
            errorMessage is { Length: > 300 } ? errorMessage[..300] : errorMessage);

        return response.StatusCode == HttpStatusCode.TooManyRequests || status >= 500
            ? Errors.AiUnavailable()
            : Errors.AiFailed("AI_REQUEST_REJECTED");
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
