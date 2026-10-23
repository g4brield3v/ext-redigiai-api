using System.Text.Json.Nodes;
using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Grading;

namespace RedigiAI.Infrastructure.Anthropic;

/// <summary>
/// Correção pela API do Claude. As partes fixas vão primeiro e entram no cache: instruções, grade, manual e
/// regras de escrita (iguais para o vestibular e tipo) e depois a proposta (igual para o tema). A redação vem
/// por último. A saída é sempre a ferramenta; o que vier nela é validado pelo backend.
/// </summary>
internal sealed class ClaudeEssayGrader(ClaudeMessagesApi api) : IEssayGrader
{
    private const string ToolName = "registrar_correcao";

    public async Task<GradingCall> GradeAsync(GradingRequest request, CancellationToken ct)
    {
        using var document = await api.SendAsync(BuildRequest(request), "grading", ct);
        var root = document.RootElement;
        return new GradingCall(
            ClaudeMessagesApi.ToolInputOf(root, ToolName)?.GetRawText(),
            request.Model,
            ClaudeMessagesApi.TokensOf(root),
            ClaudeMessagesApi.IsTruncated(root));
    }

    private static JsonObject BuildRequest(GradingRequest request) =>
        new()
        {
            ["model"] = request.Model,
            // Teto de segurança, não meta: a resposta cortada nunca é completada (docs/08-ajustes-correcao.md, item 1).
            ["max_tokens"] = request.MaxTokens,
            ["system"] = new JsonArray(Cached(request.System)),
            ["tools"] = new JsonArray(new JsonObject
            {
                ["name"] = ToolName,
                ["description"] = "Registra a correção da redação: nota zero, nível de cada critério, marcas, problema principal e próximos passos.",
                ["input_schema"] = Schema(request.Schema),
            }),
            // Os modelos atuais não aceitam forçar a ferramenta; a instrução exige o uso e a resposta sem ela é recusada.
            ["tool_choice"] = new JsonObject { ["type"] = "auto" },
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(
                    Cached(request.Proposal),
                    new JsonObject { ["type"] = "text", ["text"] = request.Essay }),
            }),
        };

    /// <summary>Marca o fim de um trecho que se repete entre chamadas, para o cache de prompt.</summary>
    private static JsonObject Cached(string text) =>
        new()
        {
            ["type"] = "text",
            ["text"] = text,
            ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
        };

    // Não existe campo de nota: additionalProperties é falso em todos os objetos.
    private static JsonObject Schema(GradingSchema schema)
    {
        JsonObject Integer(int minimum, int? maximum = null)
        {
            var integer = new JsonObject { ["type"] = "integer", ["minimum"] = minimum };
            if (maximum is { } max)
            {
                integer["maximum"] = max;
            }

            return integer;
        }

        JsonObject Text() => new() { ["type"] = "string" };
        JsonObject OptionalText() => new() { ["type"] = new JsonArray("string", "null") };
        JsonObject LearningText() => new() { ["type"] = new JsonArray("string", "null"), ["maxLength"] = Correction.MaxLearningTextLength };
        JsonObject Line() => Integer(1, schema.SheetLines);
        JsonObject Criterion() => Integer(1, schema.CriteriaCount);

        // Problema principal e próximos passos: o critério é nulo com nota zero ou quando o passo é geral.
        JsonObject Point() => new()
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("text", "criterion"),
            ["properties"] = new JsonObject
            {
                ["text"] = Text(),
                ["criterion"] = new JsonObject
                {
                    ["type"] = new JsonArray("integer", "null"), ["minimum"] = 1, ["maximum"] = schema.CriteriaCount,
                },
            },
        };

        // Exemplo de reescrita de um critério: os pedaços mantidos e tirados, na ordem, são o texto do aluno.
        JsonObject Example() => new()
        {
            ["type"] = new JsonArray("object", "null"),
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("line_label", "segments"),
            ["properties"] = new JsonObject
            {
                ["line_label"] = new JsonObject { ["type"] = "string", ["maxLength"] = Correction.MaxAskLabelLength },
                ["segments"] = new JsonObject
                {
                    ["type"] = "array",
                    ["maxItems"] = Correction.MaxExampleSegments,
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["required"] = new JsonArray("text", "kind"),
                        ["properties"] = new JsonObject
                        {
                            ["text"] = new JsonObject { ["type"] = "string", ["maxLength"] = Correction.MaxLearningTextLength },
                            ["kind"] = new JsonObject { ["enum"] = new JsonArray("keep", "remove", "add") },
                        },
                    },
                },
            },
        };

        var reasons = new JsonArray();
        foreach (var reason in schema.ZeroReasons)
        {
            reasons.Add(reason);
        }

        reasons.Add(null);

        var rules = new JsonArray();
        foreach (var code in schema.RuleCodes)
        {
            rules.Add(code);
        }

        rules.Add(null);

        return new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("zero", "criteria", "marks", "strengths", "main_issue", "next_steps"),
            ["properties"] = new JsonObject
            {
                ["zero"] = new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["required"] = new JsonArray("applies", "reason", "explanation"),
                    ["properties"] = new JsonObject
                    {
                        ["applies"] = new JsonObject { ["type"] = "boolean" },
                        ["reason"] = new JsonObject { ["enum"] = reasons },
                        ["explanation"] = OptionalText(),
                    },
                },
                ["criteria"] = new JsonObject
                {
                    ["type"] = "array",
                    ["maxItems"] = schema.CriteriaCount,
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["required"] = new JsonArray("position", "level", "justification", "lines", "guidance", "plain_explanation", "example"),
                        ["properties"] = new JsonObject
                        {
                            ["position"] = Criterion(),
                            ["level"] = Integer(1, schema.MaxLevel),
                            ["justification"] = Text(),
                            ["lines"] = new JsonObject { ["type"] = "array", ["items"] = Line() },
                            ["guidance"] = OptionalText(),
                            ["plain_explanation"] = LearningText(),
                            ["example"] = Example(),
                        },
                    },
                },
                ["marks"] = new JsonObject
                {
                    // Todos os trechos que afetam a nota e os principais acertos; o teto só segura o caso absurdo.
                    ["type"] = "array",
                    ["maxItems"] = Correction.MaxMarks,
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["required"] = new JsonArray("criterion", "kind", "line", "start", "end", "note", "rule", "why", "answer", "ask_label"),
                        ["properties"] = new JsonObject
                        {
                            ["criterion"] = Criterion(),
                            ["kind"] = new JsonObject { ["enum"] = new JsonArray("good", "issue") },
                            ["line"] = Line(),
                            ["start"] = Integer(0),
                            ["end"] = Integer(1),
                            ["note"] = OptionalText(),
                            ["rule"] = new JsonObject { ["enum"] = rules },
                            ["why"] = LearningText(),
                            ["answer"] = LearningText(),
                            ["ask_label"] = new JsonObject
                            {
                                ["type"] = new JsonArray("string", "null"), ["maxLength"] = Correction.MaxAskLabelLength,
                            },
                        },
                    },
                },
                ["strengths"] = Text(),
                ["main_issue"] = Point(),
                ["next_steps"] = new JsonObject { ["type"] = "array", ["maxItems"] = Correction.MaxNextSteps, ["items"] = Point() },
            },
        };
    }
}
