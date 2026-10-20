using System.Text.Json;
using RedigiAI.Infrastructure.Clerk;

namespace RedigiAI.UnitTests.Accounts;

public class AdminAccessTests
{
    [Theory]
    [InlineData("verified", true)]
    [InlineData("unverified", false)]
    [InlineData("expired", false)]
    public void Reads_the_primary_email_and_whether_it_is_verified(string status, bool verified)
    {
        var user = Parse($$"""
            {
              "id": "user_1",
              "primary_email_address_id": "idn_2",
              "email_addresses": [
                { "id": "idn_1", "email_address": "antigo@example.com", "verification": { "status": "verified" } },
                { "id": "idn_2", "email_address": "principal@example.com", "verification": { "status": "{{status}}" } }
              ]
            }
            """);

        var email = ClerkUserReader.ReadPrimaryEmail(user);

        Assert.Equal("principal@example.com", email!.Address);
        Assert.Equal(verified, email.Verified);
    }

    [Theory]
    [InlineData("""{ "primary_email_address_id": "idn_1", "email_addresses": [{ "id": "idn_1", "email_address": "a@example.com" }] }""")]
    [InlineData("""{ "primary_email_address_id": "idn_1", "email_addresses": [{ "id": "idn_1", "email_address": "a@example.com", "verification": null }] }""")]
    public void Email_without_verification_is_not_verified(string json)
    {
        Assert.False(ClerkUserReader.ReadPrimaryEmail(Parse(json))!.Verified);
    }

    [Theory]
    [InlineData("""{ "primary_email_address_id": null, "email_addresses": [] }""")]
    [InlineData("""{ "primary_email_address_id": "idn_9", "email_addresses": [{ "id": "idn_1", "email_address": "a@example.com" }] }""")]
    [InlineData("""{ "email_addresses": [{ "id": "idn_1", "email_address": "a@example.com" }] }""")]
    [InlineData("""{ "primary_email_address_id": "idn_1" }""")]
    [InlineData("""[]""")]
    public void Account_without_a_primary_email_has_none(string json)
    {
        Assert.Null(ClerkUserReader.ReadPrimaryEmail(Parse(json)));
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
