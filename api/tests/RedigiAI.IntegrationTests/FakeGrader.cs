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
    /// (pesos 3, 2, 3 e 2) somam 27 de 40.
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
                    ["note"] = "\"A partir\", sem crase", ["rule"] = "CRASE_VERBO",
                },
                new JsonObject
                {
                    ["criterion"] = 1, ["kind"] = "good", ["line"] = 1, ["start"] = 0, ["end"] = 1,
                    ["note"] = null, ["rule"] = null,
                }),
            ["strengths"] = "A posição está clara desde a introdução.",
            ["main_issue"] = "O segundo parágrafo repete o primeiro.",
            ["next_steps"] = new JsonArray("Troque o segundo parágrafo por um argumento novo.", "Corrija a crase da linha 1."),
        };

    public static JsonObject Zero(string reason) =>
        new()
        {
            ["zero"] = new JsonObject { ["applies"] = true, ["reason"] = reason, ["explanation"] = "Há um nome escrito no fim da folha." },
            ["criteria"] = new JsonArray(),
            ["marks"] = new JsonArray(),
            ["strengths"] = "O texto tem começo, meio e fim.",
            ["main_issue"] = "A folha identifica quem escreveu.",
            ["next_steps"] = new JsonArray("Não escreva o seu nome na folha."),
        };

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

    public Task<GradingCall> GradeAsync(GradingRequest request, CancellationToken ct)
    {
        LastRequest = request;
        Requests.Enqueue(request);
        var toolInput = Script.TryDequeue(out var next) ? next() : Good().ToJsonString();
        return Task.FromResult(new GradingCall(toolInput, request.Model, Tokens));
    }
}
