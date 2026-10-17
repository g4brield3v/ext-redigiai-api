using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Common;

namespace RedigiAI.Infrastructure.Anthropic;

public sealed class AnthropicOptions
{
    public const string Section = "Anthropic";

    public string ApiKey { get; set; } = "";

    public string BaseUrl { get; set; } = "https://api.anthropic.com";
}

/// <summary>
/// Chamada à API de mensagens do Claude, comum à transcrição e à correção: envio em streaming, tempo limite,
/// tradução dos erros em retentável ou definitivo e leitura do consumo.
/// </summary>
internal sealed class ClaudeMessagesApi(IOptions<AnthropicOptions> options, ILogger<ClaudeMessagesApi> logger)
{
    /// <summary>Tempo limite de cada chamada, do envio ao fim do streaming (docs/08-ajustes-correcao.md, item 1).</summary>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(10);

    // Um cliente para a vida do processo. O tempo limite é o de cada chamada (CallTimeout), que cobre também a
    // leitura do streaming; o do HttpClient só valeria até chegarem os cabeçalhos.
    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>
    /// Envia o pedido com <c>stream: true</c> (a API exige streaming para respostas longas) e devolve a mensagem
    /// final montada a partir dos eventos, no formato da resposta sem streaming.
    /// </summary>
    /// <param name="purpose">Para o log: "transcription" ou "grading".</param>
    /// <exception cref="AppError">
    /// <c>AI_UNAVAILABLE</c> (retentável) para 429, 5xx, sobrecarga, conexão perdida e tempo esgotado;
    /// <c>AI_REQUEST_REJECTED</c> quando a API recusa o pedido.
    /// </exception>
    public async Task<JsonDocument> SendAsync(JsonObject body, string purpose, CancellationToken ct)
    {
        var settings = options.Value;
        if (settings.ApiKey.Length == 0)
        {
            throw new InvalidOperationException("Anthropic is not configured: set Anthropic__ApiKey.");
        }

        body["stream"] = true;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.BaseUrl.TrimEnd('/')}/v1/messages")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("x-api-key", settings.ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");

        var progress = new ClaudeStreamProgress();
        using var call = CancellationTokenSource.CreateLinkedTokenSource(ct);
        call.CancelAfter(CallTimeout);
        try
        {
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, call.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw await FailureOfAsync(response, purpose, call.Token);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(call.Token);
            return await ClaudeStream.ReadAsync(stream, progress, call.Token);
        }
        catch (ClaudeStreamError error)
        {
            logger.LogWarning(
                "Claude {Purpose} stream failed ({ErrorType}): {ErrorMessage}",
                purpose,
                error.ErrorType,
                error.Message is { Length: > 300 } message ? message[..300] : error.Message);
            throw Charged(
                error.ErrorType is "overloaded_error" or "api_error" or "rate_limit_error" or "malformed_event"
                    ? Errors.AiUnavailable()
                    : Errors.AiFailed("AI_REQUEST_REJECTED"),
                progress,
                body);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException
            || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            // Conexão perdida no meio do streaming ou os 10 minutos esgotados: vale tentar de novo.
            logger.LogWarning("Claude {Purpose} call did not complete: {Error}", purpose, ex.GetType().Name);
            throw Charged(Errors.AiUnavailable(), progress, body);
        }
    }

    /// <summary>
    /// A falha leva junto o consumo, se a IA já tinha começado a responder: a entrada como a API informou e a
    /// saída pelo total informado ou, sem ele, pelo <c>max_tokens</c> do pedido (o pior caso).
    /// </summary>
    private static Exception Charged(AppError error, ClaudeStreamProgress progress, JsonObject body)
    {
        if (!progress.Started)
        {
            return error;
        }

        int Read(string name) => progress.Usage[name]?.GetValue<int>() ?? 0;
        var output = progress.OutputKnown ? Read("output_tokens") : body["max_tokens"]?.GetValue<int>() ?? 0;
        return new AiCallFailedException(
            error, new AiTokens(Read("input_tokens"), output, Read("cache_read_input_tokens"), Read("cache_creation_input_tokens")));
    }

    /// <summary>A resposta parou no <c>max_tokens</c>: o que veio está incompleto e nunca é completado.</summary>
    public static bool IsTruncated(JsonElement root) =>
        root.TryGetProperty("stop_reason", out var stop) && stop.ValueKind == JsonValueKind.String && stop.GetString() == "max_tokens";

    /// <returns>
    /// O que a IA passou à ferramenta; <c>null</c> quando ela não usou a ferramenta ou a resposta foi cortada pelo
    /// limite de tokens, que não é confiável.
    /// </returns>
    public static JsonElement? ToolInputOf(JsonElement root, string toolName)
    {
        if (IsTruncated(root))
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
                && block.TryGetProperty("name", out var name) && name.GetString() == toolName
                && block.TryGetProperty("input", out var input))
            {
                return input;
            }
        }

        return null;
    }

    public static AiTokens TokensOf(JsonElement root)
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
    private async Task<AppError> FailureOfAsync(HttpResponseMessage response, string purpose, CancellationToken ct)
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
            "Claude {Purpose} call failed with {Status} ({ErrorType}): {ErrorMessage}",
            purpose,
            status,
            errorType,
            errorMessage is { Length: > 300 } ? errorMessage[..300] : errorMessage);

        return response.StatusCode == HttpStatusCode.TooManyRequests || status >= 500
            ? Errors.AiUnavailable()
            : Errors.AiFailed("AI_REQUEST_REJECTED");
    }
}
