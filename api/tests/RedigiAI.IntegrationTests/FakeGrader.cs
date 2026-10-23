using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Common;

namespace RedigiAI.IntegrationTests;

/// <summary>
/// Faz o papel do Claude na correção: devolve o JSON que o teste roteirizar, bom ou ruim. Nenhum teste chama a
/// API de verdade.
/// </summary>
public sealed class FakeGrader : IEssayGrader
{
    /// <summary>Consumo de uma chamada típica: partes fixas lidas do cache, redação e saída cobradas.</summary>
    public static readonly AiTokens Tokens = new(Input: 1200, Output: 2500, CacheRead: 7000, CacheWrite: 0);

    /// <summary>Respostas das próximas chamadas, na ordem; sem roteiro, a correção sai boa.</summary>
    public ConcurrentQueue<Func<string?>> Script { get; } = new();

    public ConcurrentQueue<GradingRequest> Requests { get; } = new();

    /// <summary>O último pedido recebido, para conferir as camadas, a ordem de cache e o modelo.</summary>
    public GradingRequest? LastRequest { get; private set; }

    /// <summary>
    /// Correção válida para qualquer redação com texto na linha 1: níveis 3, 3, 2 e 3, que na grade da Fuvest
    /// (pesos 3, 2, 3 e 2) somam 27 de 40. A linha 1 precisa ter ao menos 12 caracteres.
    /// </summary>
    public static JsonObject Good() =>
        new()
        {
            ["zero"] = new JsonObject { ["applies"] = false, ["reason"] = null, ["explanation"] = null },
            ["criteria"] = new JsonArray(Criterion(1, 3), Criterion(2, 3), Criterion(3, 2), Criterion(4, 3)),
            ["marks"] = new JsonArray(
                new JsonObject
                {
                    ["criterion"] = 4, ["kind"] = "issue", ["line"] = 1, ["start"] = 0, ["end"] = 1,
                    ["note"] = "Sobra o acento grave no começo da linha.", ["rule"] = "CRASE_VERBO",
                    ["why"] = Why, ["answer"] = "A partir", ["ask_label"] = "Escreva a forma correta",
                },
                new JsonObject
                {
                    ["criterion"] = 1, ["kind"] = "good", ["line"] = 1, ["start"] = 2, ["end"] = 12,
                    ["note"] = null, ["rule"] = null, ["why"] = null, ["answer"] = null, ["ask_label"] = null,
                }),
            ["strengths"] = "A posição está clara desde a introdução.",
            ["main_issue"] = Point("O segundo parágrafo repete o primeiro.", 3),
            // Fora da ordem de ganho: quem ordena é o backend.
            ["next_steps"] = new JsonArray(
                Point("Corrija a crase da linha 1.", 4), Point("Troque o segundo parágrafo por um argumento novo.", 3)),
        };

    /// <summary>Uma regra geral que não entrega resposta alguma, para os desvios dos roteiros.</summary>
    public const string Why = "Antes de verbo não há artigo; sem artigo, não há crase.";

    /// <summary>Um desvio completo, com a regra geral e a resposta.</summary>
    public static JsonObject Issue(int criterion, int line, int start, int end, string? rule = null, string answer = "Outra forma.") =>
        new()
        {
            ["criterion"] = criterion, ["kind"] = "issue", ["line"] = line, ["start"] = start, ["end"] = end,
            ["note"] = "Apontamento.", ["rule"] = rule, ["why"] = Why, ["answer"] = answer, ["ask_label"] = null,
        };

    public static JsonObject Zero(string reason) =>
        new()
        {
            ["zero"] = new JsonObject { ["applies"] = true, ["reason"] = reason, ["explanation"] = "Há um nome escrito no fim da folha." },
            ["criteria"] = new JsonArray(),
            ["marks"] = new JsonArray(),
            ["strengths"] = "O texto tem começo, meio e fim.",
            ["main_issue"] = Point("A folha identifica quem escreveu.", null),
            ["next_steps"] = new JsonArray(Point("Não escreva o seu nome na folha.", null)),
        };

    public static JsonObject Point(string text, int? criterion) =>
        new() { ["text"] = text, ["criterion"] = criterion };

    public static JsonObject Criterion(int position, int level) =>
        new()
        {
            ["position"] = position,
            ["level"] = level,
            ["justification"] = "A linha 1 sustenta a avaliação.",
            ["lines"] = new JsonArray(1),
            ["guidance"] = "Reescreva o trecho da linha 1 com um dado da coletânea.",
        };

    public static string? Unavailable() => throw Errors.AiUnavailable();

    /// <summary>
    /// No roteiro, simula a resposta cortada no <c>max_tokens</c> (<c>stop_reason = "max_tokens"</c>): a saída
    /// cobrada é o <c>max_tokens</c> inteiro, como na API.
    /// </summary>
    public const string TruncatedMarker = "<<max_tokens>>";

    /// <summary>No roteiro, simula a conexão perdida no meio do streaming, com a entrada e a saída cobradas.</summary>
    public const string FailedMidStreamMarker = "<<mid_stream>>";

    public static string? Truncated() => TruncatedMarker;

    public static string? FailedMidStream() => FailedMidStreamMarker;

    /// <summary>Se definido, a correção espera por ele antes de responder (para testar a trava do job).</summary>
    public TaskCompletionSource? Hold { get; set; }

    public async Task<GradingCall> GradeAsync(GradingRequest request, CancellationToken ct)
    {
        LastRequest = request;
        Requests.Enqueue(request);
        if (Hold is { } hold)
        {
            await hold.Task.WaitAsync(ct);
        }

        var toolInput = Script.TryDequeue(out var next) ? next() : Good().ToJsonString();
        return toolInput switch
        {
            TruncatedMarker => new GradingCall(null, request.Model, Tokens with { Output = request.MaxTokens }, Truncated: true),
            FailedMidStreamMarker => throw new AiCallFailedException(Errors.AiUnavailable(), Tokens with { Output = request.MaxTokens }),
            _ => new GradingCall(toolInput, request.Model, Tokens),
        };
    }
}
