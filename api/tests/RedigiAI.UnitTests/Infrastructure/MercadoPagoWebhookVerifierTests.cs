using System.Security.Cryptography;
using System.Text;
using RedigiAI.Infrastructure.MercadoPago;

namespace RedigiAI.UnitTests.Infrastructure;

public class MercadoPagoWebhookVerifierTests
{
    private const string Secret = "segredo-de-teste";
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 15, 0, 0, TimeSpan.Zero);
    private static readonly string Ts = Now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Sign(string manifest, string secret = Secret) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(manifest)));

    private static bool IsValid(string? signature, string? requestId, string resourceId, DateTimeOffset? now = null) =>
        MercadoPagoWebhookVerifier.IsValid(Secret, signature, requestId, resourceId, now ?? Now);

    [Fact]
    public void A_signature_over_id_request_and_timestamp_is_valid()
    {
        var v1 = Sign($"id:123456;request-id:req-9;ts:{Ts};");

        Assert.True(IsValid($"ts={Ts},v1={v1}", "req-9", "123456"));
    }

    [Fact]
    public void An_alphanumeric_id_is_signed_in_lowercase()
    {
        var v1 = Sign($"id:abc123;request-id:req-9;ts:{Ts};");

        Assert.True(IsValid($"ts={Ts}, v1={v1}", "req-9", "ABC123"));
    }

    [Fact]
    public void Without_the_request_id_its_part_leaves_the_template()
    {
        var v1 = Sign($"id:123;ts:{Ts};");

        Assert.True(IsValid($"ts={Ts},v1={v1}", null, "123"));
    }

    [Fact]
    public void A_timestamp_in_milliseconds_is_accepted()
    {
        var ms = Now.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var v1 = Sign($"id:123;request-id:req-9;ts:{ms};");

        Assert.True(IsValid($"ts={ms},v1={v1}", "req-9", "123"));
    }

    [Theory]
    [InlineData(-11)]
    [InlineData(11)]
    public void A_signature_made_more_than_ten_minutes_away_is_refused(int minutes)
    {
        var v1 = Sign($"id:123;request-id:req-9;ts:{Ts};");

        // A mesma notificação, reenviada depois (ou com um relógio muito diferente), não vale mais.
        Assert.False(IsValid($"ts={Ts},v1={v1}", "req-9", "123", Now.AddMinutes(minutes)));
        Assert.True(IsValid($"ts={Ts},v1={v1}", "req-9", "123", Now.AddMinutes(minutes > 0 ? 9 : -9)));
    }

    [Theory]
    [InlineData("outra-chave", "123", "req-9")] // chave errada
    [InlineData(Secret, "124", "req-9")] // outro pagamento
    [InlineData(Secret, "123", "req-8")] // outra requisição
    public void A_signature_for_anything_else_is_refused(string secret, string resourceId, string requestId)
    {
        var v1 = Sign($"id:123;request-id:req-9;ts:{Ts};", secret);

        Assert.False(IsValid($"ts={Ts},v1={v1}", requestId, resourceId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v1=abc")]
    [InlineData("ts=1")]
    [InlineData("ts=abc,v1=0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("ts=1,v1=zz")]
    [InlineData("ts=1,v1=zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void A_missing_or_malformed_signature_is_refused(string? signature)
    {
        Assert.False(IsValid(signature, "req-9", "123"));
    }

    [Fact]
    public void Without_a_configured_secret_nothing_is_accepted()
    {
        var v1 = Sign($"id:123;request-id:req-9;ts:{Ts};", "");

        Assert.False(MercadoPagoWebhookVerifier.IsValid("", $"ts={Ts},v1={v1}", "req-9", "123", Now));
    }
}
