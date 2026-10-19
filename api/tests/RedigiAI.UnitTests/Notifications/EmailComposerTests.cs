using Microsoft.Extensions.Options;
using RedigiAI.Application.Notifications;
using RedigiAI.Domain.Billing;

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
    public void Approved_purchase_notice_has_the_amount_the_corrections_and_the_refund_deadline()
    {
        var paymentId = Guid.CreateVersion7();
        // Aprovada em 19/10, 15h em Brasília: o arrependimento vai até 26/10.
        var deadline = new DateTimeOffset(2026, 10, 26, 18, 0, 0, TimeSpan.Zero);

        var email = Composer().ComposePayment(
            EmailTemplates.PaymentApproved, new PaymentEmailFacts("aluna@exemplo.com", "Ana", paymentId, PaymentKind.Single, 3, 4800, deadline));

        Assert.Equal("Pagamento aprovado: 3 correções avulsas", email.Subject);
        Assert.Contains("Seu pagamento foi aprovado", email.Html);
        Assert.Contains("Recebemos o pagamento de R$ 48,00 por 3 correções avulsas. Elas já estão no seu saldo e não vencem.", email.Text);
        Assert.Contains("Se mudar de ideia, você pode desistir da compra até 26/10 e receber o valor das correções que não usou.", email.Text);
        Assert.Contains("Ir para o painel: https://redigiai.com.br/painel", email.Text);
        Assert.Equal(paymentId, email.RefId);
    }

    [Fact]
    public void A_single_correction_purchase_reads_in_the_singular()
    {
        var email = Composer().ComposePayment(
            EmailTemplates.PaymentApproved,
            new PaymentEmailFacts("aluna@exemplo.com", null, Guid.CreateVersion7(), PaymentKind.Single, 1, 1600, DateTimeOffset.UtcNow.AddDays(7)));

        Assert.Equal("Pagamento aprovado: 1 correção avulsa", email.Subject);
        Assert.Contains("R$ 16,00 por 1 correção avulsa. Ela já está no seu saldo e não vence.", email.Text);
        Assert.Contains("Olá,", email.Text);
    }

    [Fact]
    public void Approved_plan_charge_says_the_12_corrections_reset_at_the_next_charge()
    {
        // 15/11 às 12h UTC: 15/11 em Brasília.
        var until = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

        var email = Composer().ComposePayment(
            EmailTemplates.PaymentApproved,
            new PaymentEmailFacts("aluna@exemplo.com", "Ana", Guid.CreateVersion7(), PaymentKind.Plan, 12, 3990, until, until));

        Assert.Equal("Seu plano mensal está ativo", email.Subject);
        Assert.Contains(
            "Recebemos o pagamento de R$ 39,90. Você tem 12 correções para usar até 15/11. Nessa data o plano renova e o seu saldo do plano volta a 12; as correções que sobrarem não passam para o mês seguinte.",
            email.Text);
        Assert.Contains("Ir para o painel: https://redigiai.com.br/painel", email.Text);
    }

    [Fact]
    public void Refused_plan_charge_names_the_card_and_until_when_the_corrections_last()
    {
        var until = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

        var email = Composer().ComposePayment(
            EmailTemplates.SubscriptionPaymentFailed,
            new PaymentEmailFacts("aluna@exemplo.com", "Ana", Guid.CreateVersion7(), PaymentKind.Plan, 12, 3990, until, until, "4242"));
        var unknownCard = Composer().ComposePayment(
            EmailTemplates.SubscriptionPaymentFailed,
            new PaymentEmailFacts("aluna@exemplo.com", "Ana", Guid.CreateVersion7(), PaymentKind.Plan, 12, 3990, until));

        Assert.Equal("Não conseguimos cobrar o seu plano mensal", email.Subject);
        Assert.Contains(
            "A cobrança de R$ 39,90 no cartão com final 4242 foi recusada. Enquanto ela não for regularizada, o plano não renova.", email.Text);
        Assert.Contains("As correções que você já tem valem até 15/11.", email.Text);
        Assert.Contains("Gerenciar assinatura: https://redigiai.com.br/perfil", email.Text);
        Assert.Contains("A cobrança de R$ 39,90 no seu cartão foi recusada.", unknownCard.Text);
        Assert.DoesNotContain("valem até", unknownCard.Text);
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
