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
/// Chamada à API de mensagens do Claude, comum à transcrição e à correção: envio, tempo limite, tradução dos
/// erros em retentável ou definitivo e leitura do consumo.
/// </summary>
internal sealed class ClaudeMessagesApi(IOptions<AnthropicOptions> options, ILogger<ClaudeMessagesApi> logger)
{
    // Um cliente para a vida do processo. O tempo limite de cada chamada é o da especificação: 3 minutos.
    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = TimeSpan.FromMinutes(3),
    };

    /// <param name="purpose">Para o log: "transcription" ou "grading".</param>
    /// <exception cref="AppError">
    /// <c>AI_UNAVAILABLE</c> (retentável) para 429, 5xx, sobrecarga e tempo esgotado; <c>AI_REQUEST_REJECTED</c>
    /// quando a API recusa o pedido.
    /// </exception>
    public async Task<JsonDocument> SendAsync(JsonObject body, string purpose, CancellationToken ct)
    {
        var settings = options.Value;
        if (settings.ApiKey.Length == 0)
        {
            throw new InvalidOperationException("Anthropic is not configured: set Anthropic__ApiKey.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.BaseUrl.TrimEnd('/')}/v1/messages")
        {
            Content = JsonContent.Create(body),
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
            logger.LogWarning("Claude {Purpose} call did not complete: {Error}", purpose, ex.GetType().Name);
            throw Errors.AiUnavailable();
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw await FailureOfAsync(response, purpose, ct);
            }

            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        }
    }

    /// <returns>
    /// O que a IA passou à ferramenta; <c>null</c> quando ela não usou a ferramenta ou a resposta foi cortada pelo
    /// limite de tokens, que não é confiável.
    /// </returns>
    public static JsonElement? ToolInputOf(JsonElement root, string toolName)
    {
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
