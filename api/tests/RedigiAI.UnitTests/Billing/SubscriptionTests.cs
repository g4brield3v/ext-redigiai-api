using RedigiAI.Domain.Billing;

namespace RedigiAI.UnitTests.Billing;

public class SubscriptionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_subscription_starts_active()
    {
        var subscription = Subscription.Start(Guid.NewGuid(), "pre-1", Now.AddMonths(1), "4242", Now);

        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.Equal("4242", subscription.CardLast4);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void A_subscription_needs_the_preapproval_id(string preapprovalId)
    {
        Assert.Throws<ArgumentException>(() => Subscription.Start(Guid.NewGuid(), preapprovalId, null, null, Now));
    }

    [Theory]
    [InlineData("424")]
    [InlineData("42a2")]
    [InlineData("42424")]
    public void The_card_digits_are_exactly_four_digits(string last4)
    {
        Assert.Throws<ArgumentException>(() => Subscription.Start(Guid.NewGuid(), "pre-1", null, last4, Now));
    }

    [Fact]
    public void Expiry_is_a_negative_entry_for_what_was_left()
    {
        var bucket = CreditBucket.Grant(Guid.NewGuid(), CreditSource.Plan, 12, Now.AddMonths(1), Now);

        var entry = CreditLedgerEntry.ForExpiry(bucket, 5, Now.AddMonths(1));

        Assert.Equal(-5, entry.Delta);
        Assert.Equal(CreditSource.Plan, entry.Source);
        Assert.Equal(bucket.Id, entry.BucketId);
        Assert.Null(entry.EssayId);
    }
}
