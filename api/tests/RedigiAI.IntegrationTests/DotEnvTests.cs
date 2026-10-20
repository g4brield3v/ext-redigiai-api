using RedigiAI.Api;

namespace RedigiAI.IntegrationTests;

/// <summary>O leitor do .env local: aspas em volta do valor não fazem parte dele.</summary>
public class DotEnvTests
{
    [Theory]
    [InlineData("APP_USR-123", "APP_USR-123")]
    [InlineData("\"APP_USR-123\"", "APP_USR-123")]
    [InlineData("'APP_USR-123'", "APP_USR-123")]
    [InlineData("\"sem fechar", "\"sem fechar")]
    [InlineData("\"", "\"")]
    [InlineData("a\"b", "a\"b")]
    public void Surrounding_quotes_are_not_part_of_the_value(string raw, string expected) =>
        Assert.Equal(expected, DotEnv.Unquote(raw));
}
