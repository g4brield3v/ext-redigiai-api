using RedigiAI.Domain.Billing;

namespace RedigiAI.UnitTests.Billing;

public class SubscriptionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_subscription_request_starts_pending_without_the_mercado_pago_id()
    {
        var subscription = Subscription.Request(Guid.NewGuid(), "key", guardianAuthorizedAt: null, Now);

        Assert.Equal(SubscriptionStatus.Pending, subscription.Status);
        Assert.Null(subscription.MpPreapprovalId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void A_subscription_request_needs_the_idempotency_key(string key)
    {
        Assert.Throws<ArgumentException>(() => Subscription.Request(Guid.NewGuid(), key, null, Now));
    }

    [Theory]
    [InlineData(SubscriptionStatus.Active, new[] { SubscriptionStatus.Pending, SubscriptionStatus.Paused })]
    [InlineData(SubscriptionStatus.Paused, new[] { SubscriptionStatus.Active })]
    [InlineData(SubscriptionStatus.Cancelled, new[] { SubscriptionStatus.Pending, SubscriptionStatus.Active, SubscriptionStatus.Paused })]
    [InlineData(SubscriptionStatus.Pending, new SubscriptionStatus[0])]
    public void Each_status_is_reached_only_from_the_expected_ones(SubscriptionStatus to, SubscriptionStatus[] from)
    {
        Assert.Equal(from, Subscription.ReachableFrom(to));
    }

    [Theory]
    [InlineData("4242", "4242")]
    [InlineData("424", null)]
    [InlineData("42a2", null)]
    [InlineData("4111111111111111", null)]
    [InlineData(null, null)]
    public void Only_four_digits_are_kept_as_the_card_ending(string? informed, string? kept)
    {
        Assert.Equal(kept, Subscription.CardLast4From(informed));
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
