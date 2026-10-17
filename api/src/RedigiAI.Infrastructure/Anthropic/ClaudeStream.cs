using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RedigiAI.Infrastructure.Anthropic;

/// <summary>
/// Erro no meio do streaming, depois do status 200: evento <c>error</c> da API ou evento que não dá para ler
/// (<c>malformed_event</c>).
/// </summary>
internal sealed class ClaudeStreamError(string? errorType, string? errorMessage) : Exception(errorMessage)
{
    /// <summary><c>overloaded_error</c>, <c>api_error</c>, <c>invalid_request_error</c>, <c>malformed_event</c>…</summary>
    public string? ErrorType { get; } = errorType;
}

/// <summary>O que já chegou do streaming. Lido também depois de uma falha, para cobrar o consumo até ali.</summary>
internal sealed class ClaudeStreamProgress
{
    /// <summary>Chegou o <c>message_start</c>: a partir daqui a chamada é cobrada.</summary>
    public bool Started { get; set; }

    /// <summary>Chegou o <c>message_delta</c> com o total de tokens de saída.</summary>
    public bool OutputKnown { get; set; }

    /// <summary>O <c>usage</c> como a API mandou até agora.</summary>
    public JsonObject Usage { get; } = new();
}

/// <summary>
/// Lê a resposta em streaming da API de mensagens (Server-Sent Events) e monta a mensagem final no mesmo formato
/// da resposta sem streaming: <c>stop_reason</c>, <c>usage</c> e <c>content</c> com os blocos <c>tool_use</c> e o
/// <c>input</c> já juntado. Assim quem lê a resposta não muda.
/// </summary>
internal static class ClaudeStream
{
    /// <param name="progress">Atualizado a cada evento, inclusive quando a leitura falha no meio.</param>
    /// <exception cref="ClaudeStreamError">A API mandou um evento <c>error</c>, ou um evento veio ilegível.</exception>
    /// <exception cref="IOException">O streaming acabou antes do <c>message_stop</c>.</exception>
    public static async Task<JsonDocument> ReadAsync(Stream stream, ClaudeStreamProgress progress, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? stopReason = null;
        var blocks = new SortedDictionary<int, Block>();
        var finished = false;

        while (!finished && await reader.ReadLineAsync(ct) is { } line)
        {
            // Só as linhas "data:" trazem o evento; "event:", comentários e linhas vazias separam os eventos.
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                using var data = JsonDocument.Parse(line.AsMemory(5).Trim());
                finished = Apply(data.RootElement, progress, blocks, ref stopReason);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                throw new ClaudeStreamError("malformed_event", ex.GetType().Name);
            }
        }

        if (!finished)
        {
            throw new IOException("The Claude stream ended before message_stop.");
        }

        var content = new JsonArray();
        foreach (var block in blocks.Values.Where(block => block.Type == "tool_use"))
        {
            var toolUse = new JsonObject { ["type"] = "tool_use", ["id"] = block.Id, ["name"] = block.Name };
            // Resposta cortada pode deixar o JSON pela metade: fica sem "input", e quem lê trata como resposta inválida.
            if (ParseInput(block.Json.ToString()) is { } input)
            {
                toolUse["input"] = input;
            }

            content.Add(toolUse);
        }

        var message = new JsonObject { ["stop_reason"] = stopReason, ["usage"] = progress.Usage.DeepClone(), ["content"] = content };
        return JsonDocument.Parse(message.ToJsonString());
    }

    /// <returns><c>true</c> no <c>message_stop</c>.</returns>
    /// <exception cref="ClaudeStreamError">Evento <c>error</c>.</exception>
    private static bool Apply(JsonElement root, ClaudeStreamProgress progress, SortedDictionary<int, Block> blocks, ref string? stopReason)
    {
        switch (root.GetProperty("type").GetString())
        {
            case "message_start":
                progress.Started = true;
                if (root.GetProperty("message").TryGetProperty("usage", out var startUsage))
                {
                    Merge(progress.Usage, startUsage);
                }

                break;
            case "content_block_start":
                var block = root.GetProperty("content_block");
                blocks[root.GetProperty("index").GetInt32()] = new Block(
                    block.GetProperty("type").GetString() ?? "",
                    block.TryGetProperty("name", out var name) ? name.GetString() : null,
                    block.TryGetProperty("id", out var id) ? id.GetString() : null);
                break;
            case "content_block_delta":
                var delta = root.GetProperty("delta");
                if (delta.GetProperty("type").GetString() == "input_json_delta"
                    && blocks.TryGetValue(root.GetProperty("index").GetInt32(), out var target))
                {
                    target.Json.Append(delta.GetProperty("partial_json").GetString());
                }

                break;
            case "message_delta":
                if (root.GetProperty("delta").TryGetProperty("stop_reason", out var stop) && stop.ValueKind == JsonValueKind.String)
                {
                    stopReason = stop.GetString();
                }

                // O output_tokens daqui é o total da resposta, não um incremento.
                if (root.TryGetProperty("usage", out var deltaUsage))
                {
                    Merge(progress.Usage, deltaUsage);
                    progress.OutputKnown = true;
                }

                break;
            case "message_stop":
                return true;
            case "error":
                var error = root.GetProperty("error");
                throw new ClaudeStreamError(
                    error.TryGetProperty("type", out var type) ? type.GetString() : null,
                    error.TryGetProperty("message", out var text) ? text.GetString() : null);
        }

        return false;
    }

    private static JsonNode? ParseInput(string json)
    {
        if (json.Length == 0)
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Merge(JsonObject usage, JsonElement values)
    {
        foreach (var value in values.EnumerateObject().Where(value => value.Value.ValueKind == JsonValueKind.Number))
        {
            usage[value.Name] = value.Value.GetInt32();
        }
    }

    private sealed class Block(string type, string? name, string? id)
    {
        public string Type { get; } = type;
        public string? Name { get; } = name;
        public string? Id { get; } = id;
        public StringBuilder Json { get; } = new();
    }
}
