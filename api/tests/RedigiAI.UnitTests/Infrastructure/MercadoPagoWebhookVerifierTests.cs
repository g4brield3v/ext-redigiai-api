using System.Security.Cryptography;
using System.Text;
using RedigiAI.Infrastructure.MercadoPago;

namespace RedigiAI.UnitTests.Infrastructure;

public class MercadoPagoWebhookVerifierTests
{
    private const string Secret = "segredo-de-teste";

    private static string Sign(string manifest, string secret = Secret) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(manifest)));

    [Fact]
    public void A_signature_over_id_request_and_timestamp_is_valid()
    {
        var v1 = Sign("id:123456;request-id:req-9;ts:1704908010;");

        Assert.True(MercadoPagoWebhookVerifier.IsValid(Secret, $"ts=1704908010,v1={v1}", "req-9", "123456"));
    }

    [Fact]
    public void An_alphanumeric_id_is_signed_in_lowercase()
    {
        var v1 = Sign("id:abc123;request-id:req-9;ts:1;");

        Assert.True(MercadoPagoWebhookVerifier.IsValid(Secret, $"ts=1, v1={v1}", "req-9", "ABC123"));
    }

    [Fact]
    public void Without_the_request_id_its_part_leaves_the_template()
    {
        var v1 = Sign("id:123;ts:1;");

        Assert.True(MercadoPagoWebhookVerifier.IsValid(Secret, $"ts=1,v1={v1}", null, "123"));
    }

    [Theory]
    [InlineData("outra-chave", "123", "req-9")] // chave errada
    [InlineData(Secret, "124", "req-9")] // outro pagamento
    [InlineData(Secret, "123", "req-8")] // outra requisição
    public void A_signature_for_anything_else_is_refused(string secret, string resourceId, string requestId)
    {
        var v1 = Sign("id:123;request-id:req-9;ts:1;", secret);

        Assert.False(MercadoPagoWebhookVerifier.IsValid(Secret, $"ts=1,v1={v1}", requestId, resourceId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v1=abc")]
    [InlineData("ts=1")]
    [InlineData("ts=1,v1=zz")]
    [InlineData("ts=1,v1=zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void A_missing_or_malformed_signature_is_refused(string? signature)
    {
        Assert.False(MercadoPagoWebhookVerifier.IsValid(Secret, signature, "req-9", "123"));
    }

    [Fact]
    public void Without_a_configured_secret_nothing_is_accepted()
    {
        var v1 = Sign("id:123;request-id:req-9;ts:1;", "");

        Assert.False(MercadoPagoWebhookVerifier.IsValid("", $"ts=1,v1={v1}", "req-9", "123"));
    }
}
