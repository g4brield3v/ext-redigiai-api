using RedigiAI.Domain.Billing;

namespace RedigiAI.UnitTests.Billing;

public class PlanCalendarTests
{
    // Brasília é UTC−3 o ano todo (sem horário de verão desde 2019).
    private static DateTimeOffset Brasilia(int year, int month, int day, int hour = 0, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.FromHours(-3));

    [Theory]
    [InlineData(19, 0, 0)] // segunda 00h em ponto
    [InlineData(19, 12, 30)] // segunda à tarde
    [InlineData(25, 23, 59)] // domingo 23h59
    public void The_week_starts_on_monday_at_midnight_in_brasilia(int day, int hour, int minute)
    {
        var weekStart = PlanCalendar.WeekStart(Brasilia(2026, 10, day, hour, minute));

        Assert.Equal(Brasilia(2026, 10, 19), weekStart);
    }

    [Fact]
    public void Monday_in_utc_that_is_still_sunday_in_brasilia_belongs_to_the_previous_week()
    {
        // Segunda 02h UTC = domingo 23h em Brasília.
        var weekStart = PlanCalendar.WeekStart(new DateTimeOffset(2026, 10, 26, 2, 0, 0, TimeSpan.Zero));

        Assert.Equal(Brasilia(2026, 10, 19), weekStart);
    }

    [Fact]
    public void Credits_of_a_monday_late_in_the_month_expire_on_the_next_first_even_mid_week()
    {
        // Segunda 28/09: a troca de mês cai na quinta, e as correções vencem no dia 1º 00h.
        Assert.Equal(Brasilia(2026, 10, 1), PlanCalendar.NextMonthStart(Brasilia(2026, 9, 28)));
    }

    [Fact]
    public void Credits_granted_on_monday_the_first_last_until_the_first_of_the_next_month()
    {
        Assert.Equal(Brasilia(2026, 7, 1), PlanCalendar.NextMonthStart(Brasilia(2026, 6, 1)));
    }

    [Fact]
    public void December_rolls_over_to_january()
    {
        Assert.Equal(Brasilia(2027, 1, 1), PlanCalendar.NextMonthStart(Brasilia(2026, 12, 28)));
    }

    [Fact]
    public void The_weekly_bucket_has_three_credits_that_expire_on_the_next_first_and_a_key_for_the_week()
    {
        var subscription = Subscription.Start(Guid.NewGuid(), "pre-1", null, "4242", Brasilia(2026, 9, 1));
        var weekStart = Brasilia(2026, 9, 28);

        var bucket = CreditBucket.GrantPlanWeek(subscription, weekStart, weekStart.AddMinutes(3));

        Assert.Equal(CreditSource.PlanWeek, bucket.Source);
        Assert.Equal(3, bucket.Granted);
        Assert.Equal(3, bucket.Remaining);
        Assert.Equal(Brasilia(2026, 10, 1), bucket.ExpiresAt);
        Assert.Equal($"plan_week:{subscription.Id}:2026-09-28", bucket.GrantKey);
    }

    [Fact]
    public void Only_active_subscriptions_that_existed_before_the_monday_earn_its_credits()
    {
        var monday = Brasilia(2026, 10, 19);
        var before = Subscription.Start(Guid.NewGuid(), "pre-before", null, null, monday.AddDays(-3));
        var sameMonday = Subscription.Start(Guid.NewGuid(), "pre-monday", null, null, monday);

        Assert.True(before.EarnsWeeklyCredits(monday));
        // A semana em que a assinatura começa já tem a concessão imediata.
        Assert.False(sameMonday.EarnsWeeklyCredits(monday));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void A_subscription_needs_the_preapproval_id(string preapprovalId)
    {
        Assert.Throws<ArgumentException>(() => Subscription.Start(Guid.NewGuid(), preapprovalId, null, null, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("424")]
    [InlineData("42a2")]
    [InlineData("42424")]
    public void The_card_digits_are_exactly_four_digits(string last4)
    {
        Assert.Throws<ArgumentException>(() => Subscription.Start(Guid.NewGuid(), "pre-1", null, last4, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Expiry_is_a_negative_entry_for_what_was_left()
    {
        var bucket = CreditBucket.Grant(Guid.NewGuid(), CreditSource.PlanWeek, 3, Brasilia(2026, 11, 1), Brasilia(2026, 10, 19));

        var entry = CreditLedgerEntry.ForExpiry(bucket, 2, Brasilia(2026, 11, 1));

        Assert.Equal(-2, entry.Delta);
        Assert.Equal(CreditSource.PlanWeek, entry.Source);
        Assert.Equal(bucket.Id, entry.BucketId);
        Assert.Null(entry.EssayId);
    }
}
