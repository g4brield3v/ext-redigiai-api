using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using RedigiAI.Application.Abstractions;
using RedigiAI.Domain.Common;

namespace RedigiAI.Application.Notifications;

/// <summary>O que o e-mail de uma redação pode mostrar. Nunca a nota nem trecho da correção (regra 8).</summary>
/// <param name="Name">Nome do aluno, se ele informou.</param>
/// <param name="IllegibleCount">Palavras ilegíveis a preencher, nos avisos da revisão.</param>
/// <param name="FailureIllegibleCount">Palavras ilegíveis da foto recusada, quando a leitura chegou a contar.</param>
/// <param name="PromptSlug">Endereço da proposta, para o botão de novo envio.</param>
public sealed record EssayEmailFacts(
    string To,
    string? Name,
    Guid EssayId,
    string EssayTitle,
    int IllegibleCount,
    DateTimeOffset? ReviewDeadlineAt,
    string? FailureCode,
    int? FailureIllegibleCount,
    string PromptSlug);

/// <summary>
/// Monta os e-mails da redação: assunto com o título "Vestibular – Tipo – Tema", um aviso curto e um botão para a
/// plataforma. Os avisos de falha usam o título e o texto do catálogo de <c>docs/08-ajustes-correcao.md</c>.
/// </summary>
public sealed class EmailComposer(IOptions<SiteOptions> site, IOptions<CompanyOptions> company)
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public EmailMessage Compose(string template, EssayEmailFacts facts)
    {
        var content = template switch
        {
            EmailTemplates.CorrectionReady => new Content(
                $"Sua correção está pronta: {facts.EssayTitle}",
                "Sua correção está pronta",
                [$"A correção da redação \"{facts.EssayTitle}\" já está na plataforma, com a nota de cada critério e o que corrigir."],
                "Ver a correção",
                $"/redacoes/{facts.EssayId}",
                CanTurnOff: true),
            EmailTemplates.IllegibleWords => new Content(
                $"{Missing(facts.IllegibleCount)} para corrigir: {facts.EssayTitle}",
                "Precisamos da sua ajuda com algumas palavras",
                [
                    $"Não conseguimos ler {Words(facts.IllegibleCount)} da redação \"{facts.EssayTitle}\". Digite só essas palavras para a correção continuar.",
                    $"O prazo vai até {Deadline(facts.ReviewDeadlineAt)} (horário de Brasília). Depois disso, a redação é cancelada e a correção volta para o seu saldo.",
                ],
                "Preencher as palavras",
                $"/redacoes/{facts.EssayId}/revisao",
                CanTurnOff: false),
            EmailTemplates.IllegibleReminder => new Content(
                $"Lembrete: {Missing(facts.IllegibleCount).ToLower(PtBr)} na redação {facts.EssayTitle}",
                "Ainda faltam algumas palavras",
                [
                    $"As palavras ilegíveis da redação \"{facts.EssayTitle}\" ainda não foram preenchidas.",
                    $"O prazo termina {Deadline(facts.ReviewDeadlineAt)} (horário de Brasília). Depois disso, a redação é cancelada e a correção volta para o seu saldo.",
                ],
                "Preencher as palavras",
                $"/redacoes/{facts.EssayId}/revisao",
                CanTurnOff: true),
            EmailTemplates.ReviewExpired => Failure(
                facts,
                "O prazo para preencher as palavras acabou",
                "As palavras ilegíveis não foram preenchidas em 24 horas, então a redação foi cancelada. A correção voltou para o seu saldo.",
                "Ir para o painel",
                "/painel"),
            EmailTemplates.PhotoUnreadable => Failure(
                facts,
                "Não conseguimos ler a sua foto",
                facts.FailureIllegibleCount is { } count
                    ? $"Mais de 10 palavras ficaram ilegíveis ({count}), então a redação não foi corrigida. A correção voltou para o seu saldo."
                    : "Mais de 10 palavras ficaram ilegíveis, então a redação não foi corrigida. A correção voltou para o seu saldo.",
                "Enviar nova foto",
                $"/propostas/{Uri.EscapeDataString(facts.PromptSlug)}/enviar"),
            EmailTemplates.EssayFailed => facts.FailureCode switch
            {
                "TRANSCRIPTION_FAILED" => Failure(
                    facts,
                    "Não conseguimos processar a foto",
                    "Houve uma falha do nosso lado ao ler a foto. A correção voltou para o seu saldo.",
                    "Ir para o painel",
                    "/painel"),
                "GRADING_FAILED" => Failure(
                    facts,
                    "A correção não foi concluída",
                    "Houve uma falha do nosso lado ao corrigir a redação. A correção voltou para o seu saldo.",
                    "Ir para o painel",
                    "/painel"),
                _ => Failure(
                    facts,
                    "Algo deu errado do nosso lado",
                    "A redação não foi corrigida e a correção voltou para o seu saldo.",
                    "Ir para o painel",
                    "/painel"),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(template), template, "Unknown e-mail template."),
        };

        // O título da proposta vem do cadastro: quebra de linha nunca entra no assunto.
        content = content with { Subject = content.Subject.ReplaceLineEndings(" ") };
        return new EmailMessage(facts.To, content.Subject, Html(facts, content), Text(facts, content), template, facts.EssayId);
    }

    private static Content Failure(EssayEmailFacts facts, string title, string text, string button, string path) =>
        new($"{title}: {facts.EssayTitle}", title, [$"Redação: {facts.EssayTitle}.", text], button, path, CanTurnOff: false);

    private static string Words(int count) => count == 1 ? "1 palavra" : $"{count} palavras";

    private static string Missing(int count) => count == 1 ? "Falta 1 palavra" : $"Faltam {count} palavras";

    private static string Deadline(DateTimeOffset? deadline) =>
        deadline is { } at
            ? BrasiliaTime.At(at).ToString("dd/MM 'às' HH'h'mm", PtBr)
            : "o fim do prazo";

    private string Link(string path) => site.Value.BaseUrl.TrimEnd('/') + path;

    private static string Greeting(EssayEmailFacts facts) =>
        string.IsNullOrWhiteSpace(facts.Name) ? "Olá," : $"Olá, {facts.Name.Trim()},";

    private IEnumerable<string> FooterLines(Content content)
    {
        if (content.CanTurnOff)
        {
            yield return "Você pode desligar este aviso em Perfil, na plataforma.";
        }

        var options = company.Value;
        var identification = new[]
        {
            options.LegalName,
            options.Cnpj.Length > 0 ? $"CNPJ {options.Cnpj}" : "",
            options.Address,
            options.SupportEmail,
        }.Where(part => part.Length > 0).ToList();

        yield return identification.Count > 0
            ? $"RedigiAI · {string.Join(" · ", identification)}"
            : "RedigiAI";
    }

    private string Text(EssayEmailFacts facts, Content content)
    {
        var text = new StringBuilder();
        text.Append(Greeting(facts)).Append("\n\n");
        foreach (var paragraph in content.Paragraphs)
        {
            text.Append(paragraph).Append("\n\n");
        }

        text.Append(content.Button).Append(": ").Append(Link(content.Path)).Append("\n\n--\n");
        foreach (var line in FooterLines(content))
        {
            text.Append(line).Append('\n');
        }

        return text.ToString();
    }

    private string Html(EssayEmailFacts facts, Content content)
    {
        // Só os caracteres especiais do HTML: o documento é UTF-8, e os acentos ficam como são.
        static string E(string value) => value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&#39;", StringComparison.Ordinal);

        var html = new StringBuilder();
        html.Append("<!doctype html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
            .Append("<title>").Append(E(content.Subject)).Append("</title></head>")
            .Append("<body style=\"margin:0;padding:0;background:#f4f4f5;font-family:Arial,Helvetica,sans-serif;color:#18181b;\">")
            // Pré-visualização na caixa de entrada: o título da redação.
            .Append("<div style=\"display:none;max-height:0;overflow:hidden;\">").Append(E(facts.EssayTitle)).Append("</div>")
            .Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background:#f4f4f5;padding:24px 12px;\"><tr><td align=\"center\">")
            .Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"max-width:560px;background:#ffffff;border-radius:12px;padding:32px 28px;\">")
            .Append("<tr><td style=\"font-size:14px;font-weight:bold;color:#4f46e5;padding-bottom:20px;\">RedigiAI</td></tr>")
            .Append("<tr><td style=\"font-size:22px;font-weight:bold;line-height:1.3;padding-bottom:16px;\">").Append(E(content.Heading)).Append("</td></tr>")
            .Append("<tr><td style=\"font-size:16px;line-height:1.5;padding-bottom:12px;\">").Append(E(Greeting(facts))).Append("</td></tr>");

        foreach (var paragraph in content.Paragraphs)
        {
            html.Append("<tr><td style=\"font-size:16px;line-height:1.5;padding-bottom:12px;\">").Append(E(paragraph)).Append("</td></tr>");
        }

        html.Append("<tr><td style=\"padding:12px 0 8px;\"><a href=\"").Append(E(Link(content.Path)))
            .Append("\" style=\"display:inline-block;background:#4f46e5;color:#ffffff;text-decoration:none;font-size:16px;font-weight:bold;padding:12px 24px;border-radius:8px;\">")
            .Append(E(content.Button)).Append("</a></td></tr>")
            .Append("</table>")
            .Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"max-width:560px;\">");

        foreach (var line in FooterLines(content))
        {
            html.Append("<tr><td style=\"font-size:12px;line-height:1.5;color:#71717a;padding:8px 28px 0;\">").Append(E(line)).Append("</td></tr>");
        }

        html.Append("</table></td></tr></table></body></html>");
        return html.ToString();
    }

    private sealed record Content(
        string Subject, string Heading, IReadOnlyList<string> Paragraphs, string Button, string Path, bool CanTurnOff);
}
