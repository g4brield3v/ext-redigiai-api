using System.Security.Cryptography;
using System.Text;
using RedigiAI.Infrastructure.Clerk;

namespace RedigiAI.UnitTests.Clerk;

public class SvixSignatureVerifierTests
{
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef");
    private static readonly string Secret = "whsec_" + Convert.ToBase64String(Key);
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 15, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"type":"user.updated"}""");

    [Fact]
    public void Accepts_a_valid_signature()
    {
        var timestamp = Now.ToUnixTimeSeconds().ToString();

        Assert.True(SvixSignatureVerifier.IsValid(Secret, "msg_1", timestamp, Sign("msg_1", timestamp, Body), Body, Now));
    }

    [Fact]
    public void Accepts_when_any_of_the_listed_signatures_matches()
    {
        var timestamp = Now.ToUnixTimeSeconds().ToString();
        var signatures = $"v1,{Convert.ToBase64String(new byte[32])} {Sign("msg_1", timestamp, Body)}";

        Assert.True(SvixSignatureVerifier.IsValid(Secret, "msg_1", timestamp, signatures, Body, Now));
    }

    [Fact]
    public void Rejects_a_tampered_body()
    {
        var timestamp = Now.ToUnixTimeSeconds().ToString();
        var tampered = Encoding.UTF8.GetBytes("""{"type":"user.deleted"}""");

        Assert.False(SvixSignatureVerifier.IsValid(Secret, "msg_1", timestamp, Sign("msg_1", timestamp, Body), tampered, Now));
    }

    [Fact]
    public void Rejects_another_message_id()
    {
        var timestamp = Now.ToUnixTimeSeconds().ToString();

        Assert.False(SvixSignatureVerifier.IsValid(Secret, "msg_2", timestamp, Sign("msg_1", timestamp, Body), Body, Now));
    }

    [Theory]
    [InlineData(-6)]
    [InlineData(6)]
    public void Rejects_a_timestamp_outside_the_five_minute_window(int minutes)
    {
        var timestamp = Now.AddMinutes(minutes).ToUnixTimeSeconds().ToString();

        Assert.False(SvixSignatureVerifier.IsValid(Secret, "msg_1", timestamp, Sign("msg_1", timestamp, Body), Body, Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v1,not-base64")]
    [InlineData("v2,AAAA")]
    public void Rejects_missing_or_malformed_signatures(string? signatures)
    {
        var timestamp = Now.ToUnixTimeSeconds().ToString();

        Assert.False(SvixSignatureVerifier.IsValid(Secret, "msg_1", timestamp, signatures, Body, Now));
    }

    [Fact]
    public void Rejects_when_the_secret_is_not_configured()
    {
        var timestamp = Now.ToUnixTimeSeconds().ToString();

        Assert.False(SvixSignatureVerifier.IsValid("", "msg_1", timestamp, Sign("msg_1", timestamp, Body), Body, Now));
    }

    private static string Sign(string id, string timestamp, byte[] body)
    {
        var content = Encoding.UTF8.GetBytes($"{id}.{timestamp}.").Concat(body).ToArray();
        return "v1," + Convert.ToBase64String(HMACSHA256.HashData(Key, content));
    }
}
