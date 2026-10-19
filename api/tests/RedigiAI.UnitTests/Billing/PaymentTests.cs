using RedigiAI.Domain.Billing;

namespace RedigiAI.UnitTests.Billing;

public class PaymentTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 15, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1, 1600)]
    [InlineData(3, 4800)]
    [InlineData(10, 16000)]
    public void The_single_purchase_costs_16_reais_per_correction(int quantity, long amountCents)
    {
        var payment = Payment.StartSingle(Guid.NewGuid(), PaymentMethod.Pix, quantity, "key", null, Now);

        Assert.Equal(amountCents, payment.AmountCents);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Equal(PaymentKind.Single, payment.Kind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void The_quantity_goes_from_1_to_10(int quantity)
    {
        Assert.ThrowsAny<ArgumentException>(() => Payment.StartSingle(Guid.NewGuid(), PaymentMethod.Pix, quantity, "key", null, Now));
    }

    [Theory]
    [InlineData(PaymentStatus.Approved, new[] { PaymentStatus.Pending })]
    [InlineData(PaymentStatus.Rejected, new[] { PaymentStatus.Pending })]
    [InlineData(PaymentStatus.Cancelled, new[] { PaymentStatus.Pending })]
    [InlineData(PaymentStatus.Refunded, new[] { PaymentStatus.Approved, PaymentStatus.Pending })]
    [InlineData(PaymentStatus.ChargedBack, new[] { PaymentStatus.Approved, PaymentStatus.Pending })]
    [InlineData(PaymentStatus.Pending, new PaymentStatus[0])]
    public void Each_status_is_reached_only_from_the_expected_ones(PaymentStatus to, PaymentStatus[] from)
    {
        Assert.Equal(from, Payment.ReachableFrom(to));
    }

    [Fact]
    public void Credits_of_a_purchase_are_single_and_never_expire_with_one_grant_per_purchase()
    {
        var payment = Payment.StartSingle(Guid.NewGuid(), PaymentMethod.Card, 4, "key", null, Now);

        var bucket = CreditBucket.GrantForPayment(payment, Now);
        var entry = CreditLedgerEntry.ForPaymentGrant(bucket, payment.Id, Now);

        Assert.Equal(CreditSource.Single, bucket.Source);
        Assert.Equal(4, bucket.Granted);
        Assert.Null(bucket.ExpiresAt);
        Assert.Equal($"payment:{payment.Id}", bucket.GrantKey);
        Assert.Equal(4, entry.Delta);
        Assert.Equal(payment.Id, entry.PaymentId);
    }

    [Fact]
    public void A_pending_purchase_is_not_refundable()
    {
        var payment = Payment.StartSingle(Guid.NewGuid(), PaymentMethod.Pix, 1, "key", null, Now);

        Assert.False(payment.RefundableOn(Now));
    }
}
