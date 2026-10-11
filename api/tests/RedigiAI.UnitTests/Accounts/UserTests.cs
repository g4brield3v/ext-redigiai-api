using RedigiAI.Domain.Accounts;
using RedigiAI.Domain.Common;

namespace RedigiAI.UnitTests.Accounts;

public class UserTests
{
    // 11/10/2026, 12h em Brasília.
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 15, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("2010-10-11", 16)] // faz 16 hoje
    [InlineData("2010-10-12", 15)] // faz 16 amanhã
    [InlineData("2008-02-29", 18)] // nascido em ano bissexto
    [InlineData("2026-10-11", 0)]
    public void Age_counts_completed_years(string birth, int expected)
    {
        var today = new DateOnly(2026, 10, 11);
        var birthDate = BirthDate.Create(DateOnly.Parse(birth), today);

        Assert.Equal(expected, birthDate.AgeOn(today));
    }

    [Fact]
    public void Leap_day_birthday_completes_the_year_on_march_first()
    {
        var birthDate = BirthDate.FromStorage(new DateOnly(2008, 2, 29));

        Assert.Equal(16, birthDate.AgeOn(new DateOnly(2025, 2, 28)));
        Assert.Equal(17, birthDate.AgeOn(new DateOnly(2025, 3, 1)));
    }

    [Fact]
    public void Registers_someone_who_turns_16_today()
    {
        var user = Register(new DateOnly(2010, 10, 11));

        Assert.Equal("user_1", user.ClerkUserId);
        Assert.Equal("1", user.TermsVersion);
        Assert.Equal(Now, user.TermsAcceptedAt);
        Assert.Equal(EmailPreferences.Default, user.EmailPreferences);
        Assert.False(user.EmailPreferences.NewThemes);
        Assert.Equal(7, user.Id.Version);
    }

    [Fact]
    public void Refuses_someone_under_16()
    {
        var error = Assert.Throws<AppError>(() => Register(new DateOnly(2010, 10, 12)));

        Assert.Equal("AGE_BELOW_MINIMUM", error.Code);
        Assert.Equal(422, error.HttpStatus);
    }

    [Fact]
    public void Age_uses_the_date_in_brasilia_not_utc()
    {
        // 01h UTC do dia 12 ainda é dia 11 em Brasília: quem faz 16 no dia 12 ainda tem 15.
        var lateNight = new DateTimeOffset(2026, 10, 12, 1, 0, 0, TimeSpan.Zero);

        var error = Assert.Throws<AppError>(() =>
            User.Register("user_1", "a@example.com", null, new DateOnly(2010, 10, 12), "1", lateNight));

        Assert.Equal("AGE_BELOW_MINIMUM", error.Code);
    }

    [Theory]
    [InlineData("2026-10-12")] // futuro
    [InlineData("1900-01-01")]
    public void Refuses_impossible_birth_dates(string birth)
    {
        var error = Assert.Throws<AppError>(() => Register(DateOnly.Parse(birth)));

        Assert.Equal("VALIDATION_ERROR", error.Code);
    }

    [Fact]
    public void Rename_trims_and_clears_blank_names()
    {
        var user = Register(new DateOnly(2000, 1, 1));

        user.Rename("  Ana Souza ");
        Assert.Equal("Ana Souza", user.Name);

        user.Rename("   ");
        Assert.Null(user.Name);

        Assert.Throws<AppError>(() => user.Rename(new string('a', User.MaxNameLength + 1)));
    }

    private static User Register(DateOnly birthDate) =>
        User.Register("user_1", "a@example.com", "Ana", birthDate, "1", Now);
}
