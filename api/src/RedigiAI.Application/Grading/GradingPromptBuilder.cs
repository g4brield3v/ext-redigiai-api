using System.Globalization;
using System.Text;
using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Catalog;
using RedigiAI.Domain.Essays;
using RedigiAI.Domain.Grading;

namespace RedigiAI.Application.Grading;

/// <summary>As camadas da base de conhecimento usadas numa correção, todas nas versões do perfil.</summary>
internal sealed record GradingLayers(
    PromptTemplate Prompt,
    RubricVersion Rubric,
    GuidelinesVersion Guidelines,
    WritingRulesVersion WritingRules,
    Exam Exam,
    EssayType EssayType,
    Prompt Proposal);

/// <summary>
/// Monta a chamada da correção preenchendo as instruções do perfil com as camadas da base de conhecimento
/// (especificação, seção 8). As partes saem na ordem de cache: vestibular e tipo, tema, redação.
/// </summary>
internal static class GradingPromptBuilder
{
    private const string EssayTag = "<redacao";

    /// <param name="identificationFound">A transcrição encontrou nome, assinatura ou outra identificação na folha.</param>
    public static GradingRequest Build(
        GradingLayers layers, IReadOnlyList<TranscribedLine> lines, bool identificationFound, string model)
    {
        var rubric = layers.Rubric;
        var system = Fill(layers.Prompt.SystemText, new Dictionary<string, string>
        {
            ["exam_name"] = layers.Exam.Name,
            ["rubric_version"] = Number(rubric.Version),
            ["max_level"] = Number(rubric.MaxLevel),
            ["criteria_with_weights_and_level_descriptors"] = Criteria(rubric),
            ["zero_rules"] = string.Join("; ", rubric.ZeroRules.Select(rule => $"{rule.Code} ({rule.Description})")),
            ["guidelines"] = Guidelines(layers.Guidelines),
            ["writing_rules"] = WritingRules(layers.WritingRules),
        });

        var proposal = layers.Proposal;
        var user = Fill(layers.Prompt.UserText ?? "", new Dictionary<string, string>
        {
            ["theme_title"] = proposal.Title,
            ["essay_type_name"] = PromptText.Genre(proposal, layers.EssayType),
            ["theme_statement"] = PromptText.Statement(proposal),
            ["theme_texts"] = "\n" + PromptText.Collection(proposal) + "\n",
            ["theme_grading_notes"] = "\n" + PromptText.GradingNotes(proposal.GradingNotes) + "\n",
            ["line_count"] = Number(lines.Count),
            ["identification_found"] = identificationFound ? "sim" : "nao",
            ["numbered_lines"] = string.Join("\n", lines.Select(line => $"{Number(line.Number)}| {AsData(line.Text)}")),
        });

        // A proposta é igual para todas as redações do tema; a redação vem por último, fora do cache.
        var split = user.IndexOf(EssayTag, StringComparison.Ordinal);
        if (split < 0)
        {
            throw new InvalidOperationException("The grading instructions do not delimit the essay with <redacao>.");
        }

        return new GradingRequest(
            system,
            user[..split].TrimEnd(),
            user[split..],
            new GradingSchema(
                rubric.Criteria.Count,
                rubric.MaxLevel,
                [.. rubric.ZeroRules.Select(rule => rule.Code)],
                layers.Exam.SheetLines,
                [.. layers.WritingRules.Rules.OrderBy(rule => rule.Position).Select(rule => rule.Code)]),
            model);
    }

    /// <summary>
    /// O texto do aluno é dado: os sinais de menor e maior viram os equivalentes de largura inteira, para nada
    /// do que ele escreveu fechar a marcação <c>&lt;redacao&gt;</c>. Cada sinal continua valendo um caractere,
    /// então as posições das marcas não mudam.
    /// </summary>
    public static string AsData(string studentText) => studentText.Replace('<', '＜').Replace('>', '＞');

    private static string Criteria(RubricVersion rubric)
    {
        var text = new StringBuilder();
        foreach (var criterion in rubric.Criteria.OrderBy(c => c.Position))
        {
            text.Append("Critério ").Append(Number(criterion.Position)).Append(" · ").Append(criterion.Name)
                .Append(" (peso ").Append(Number(criterion.Weight)).AppendLine(")");
            foreach (var (level, descriptor) in criterion.LevelDescriptors.OrderBy(pair => pair.Key))
            {
                text.Append("  Nível ").Append(Number(level)).Append(": ").AppendLine(descriptor);
            }
        }

        return text.ToString().TrimEnd();
    }

    private static string Guidelines(GuidelinesVersion guidelines)
    {
        var text = new StringBuilder(guidelines.GeneralRules.Trim()).AppendLine().AppendLine();
        foreach (var criterion in guidelines.Criteria.OrderBy(c => c.CriterionPosition))
        {
            text.Append("Critério ").Append(Number(criterion.CriterionPosition)).AppendLine(":")
                .AppendLine(criterion.Content.Trim()).AppendLine();
        }

        return text.Append(guidelines.GuidanceWriting.Trim()).ToString();
    }

    private static string WritingRules(WritingRulesVersion rules)
    {
        var text = new StringBuilder();
        foreach (var rule in rules.Rules.OrderBy(r => r.Position))
        {
            text.Append(rule.Code).Append(" · ").Append(rule.Name).Append(": ").Append(rule.Explanation)
                .Append(" Errado: ").Append(rule.WrongExample).Append(" Certo: ").AppendLine(rule.RightExample);
        }

        return string.IsNullOrWhiteSpace(rules.Notes) ? text.ToString().TrimEnd() : text.Append(rules.Notes.Trim()).ToString();
    }

    private static string Fill(string template, Dictionary<string, string> values)
    {
        var text = new StringBuilder(template);
        foreach (var (name, value) in values)
        {
            text.Replace("{{" + name + "}}", value);
        }

        return text.ToString();
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
