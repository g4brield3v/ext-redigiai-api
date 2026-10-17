using Microsoft.Extensions.Options;
using RedigiAI.Application.Notifications;

namespace RedigiAI.UnitTests.Notifications;

public class EmailComposerTests
{
    private static readonly Guid EssayId = Guid.CreateVersion7();

    private static EmailComposer Composer(CompanyOptions? company = null) =>
        new(Options.Create(new SiteOptions { BaseUrl = "https://redigiai.com.br/" }), Options.Create(company ?? new CompanyOptions()));

    private static EssayEmailFacts Facts(string title = "Fuvest – Dissertação em prosa – Algoritmos", string? failureCode = null) =>
        new("aluna@exemplo.com", "Ana", EssayId, title, 2, new DateTimeOffset(2026, 10, 18, 17, 30, 0, TimeSpan.Zero), failureCode, null, "algoritmos");

    [Fact]
    public void Title_from_the_catalog_is_escaped_in_the_html_and_never_breaks_the_subject()
    {
        var email = Composer().Compose(EmailTemplates.CorrectionReady, Facts("Fuvest – Tipo – <script>alert(1)</script>\r\nBcc: x@y.z"));

        Assert.DoesNotContain("<script>", email.Html);
        Assert.Contains("&lt;script&gt;", email.Html);
        Assert.DoesNotContain('\n', email.Subject);
        Assert.DoesNotContain('\r', email.Subject);
        Assert.Contains("https://redigiai.com.br/redacoes/", email.Html);
        Assert.DoesNotContain("br//redacoes", email.Html);
    }

    [Fact]
    public void Deadline_is_shown_in_brasilia_time()
    {
        var email = Composer().Compose(EmailTemplates.IllegibleWords, Facts());

        // 17h30 UTC = 14h30 em Brasília.
        Assert.Contains("O prazo vai até 18/10 às 14h30 (horário de Brasília)", email.Text);
        Assert.Contains("Olá, Ana,", email.Text);
    }

    [Theory]
    [InlineData("GRADING_FAILED", "A correção não foi concluída")]
    [InlineData("TRANSCRIPTION_FAILED", "Não conseguimos processar a foto")]
    [InlineData("SOMETHING_NEW", "Algo deu errado do nosso lado")]
    public void Failure_notice_follows_the_catalog_and_falls_back_to_internal_error(string code, string title)
    {
        var email = Composer().Compose(EmailTemplates.EssayFailed, Facts(failureCode: code));

        Assert.StartsWith($"{title}: ", email.Subject);
        Assert.Contains("a correção voltou para o seu saldo.", email.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(code, email.Text);
    }

    [Theory]
    [InlineData("http://redigiai.com.br", true)]
    [InlineData("https://user:pass@redigiai.com.br", false)]
    [InlineData("https://redigiai.com.br/?next=x", false)]
    [InlineData("", false)]
    [InlineData("javascript:alert(1)", false)]
    public void Site_address_that_is_not_a_plain_https_address_stops_the_email(string baseUrl, bool requireHttps)
    {
        var composer = new EmailComposer(
            Options.Create(new SiteOptions { BaseUrl = baseUrl, RequireHttps = requireHttps }), Options.Create(new CompanyOptions()));

        var refused = Assert.Throws<RedigiAI.Domain.Common.AppError>(() => composer.Compose(EmailTemplates.CorrectionReady, Facts()));
        Assert.Equal(("EMAIL_NOT_CONFIGURED", false), (refused.Code, refused.Retryable));
    }

    [Fact]
    public void Plain_http_is_accepted_only_where_https_is_not_required()
    {
        var composer = new EmailComposer(
            Options.Create(new SiteOptions { BaseUrl = "http://localhost:5173", RequireHttps = false }), Options.Create(new CompanyOptions()));

        Assert.Contains("http://localhost:5173/redacoes/", composer.Compose(EmailTemplates.CorrectionReady, Facts()).Text);
    }

    [Fact]
    public void Footer_shows_only_the_company_fields_that_are_filled()
    {
        var email = Composer(new CompanyOptions { LegalName = "RedigiAI Educação Ltda.", SupportEmail = "ajuda@redigiai.com.br" })
            .Compose(EmailTemplates.ReviewExpired, Facts());

        Assert.Contains("RedigiAI · RedigiAI Educação Ltda. · ajuda@redigiai.com.br", email.Text);
        Assert.DoesNotContain("CNPJ", email.Text);
        Assert.DoesNotContain("Você pode desligar este aviso", email.Text);
    }
}
