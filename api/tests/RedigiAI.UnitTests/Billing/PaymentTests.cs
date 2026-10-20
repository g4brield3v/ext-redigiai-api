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
    [InlineData(PaymentStatus.Approved, new[] { PaymentStatus.Pending, PaymentStatus.Rejected })]
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

    [Theory]
    [InlineData(3200, 2)]
    [InlineData(2000, 1)]
    [InlineData(1599, 0)]
    public void A_refunded_amount_covers_only_whole_single_corrections(long refundedCents, int credits)
    {
        var payment = Payment.StartSingle(Guid.NewGuid(), PaymentMethod.Pix, 4, "key", null, Now);

        Assert.Equal(credits, payment.CreditsCoveredBy(refundedCents));
    }

    [Fact]
    public void A_plan_charge_is_39_90_for_12_corrections_worth_3_33_each()
    {
        var payment = Payment.StartPlanCharge(Subscription.Request(Guid.NewGuid(), "key", null, Now), "123", Now);

        Assert.Equal(3990, payment.AmountCents);
        Assert.Equal(12, payment.Quantity);
        Assert.Equal(PaymentKind.Plan, payment.Kind);
        Assert.Equal(3, payment.CreditsCoveredBy(1000));
    }

    [Fact]
    public void The_plan_cycle_grants_12_corrections_until_the_next_charge()
    {
        var payment = Payment.StartPlanCharge(Subscription.Request(Guid.NewGuid(), "key", null, Now), "123", Now);

        var bucket = CreditBucket.GrantPlanCycle(payment, Now.AddMonths(1), Now);

        Assert.Equal(CreditSource.Plan, bucket.Source);
        Assert.Equal(12, bucket.Granted);
        Assert.Equal(Now.AddMonths(1), bucket.ExpiresAt);
        Assert.Equal($"payment:{payment.Id}", bucket.GrantKey);
    }

    [Theory]
    [InlineData(3, 4800)]
    [InlineData(1, 1600)]
    [InlineData(0, 0)]
    public void A_single_purchase_returns_16_reais_per_unused_correction(int unused, long amountCents)
    {
        var payment = Payment.StartSingle(Guid.NewGuid(), PaymentMethod.Pix, 3, "key", null, Now);

        Assert.Equal(amountCents, RefundRequest.AmountFor(payment, unused));
    }

    [Theory]
    [InlineData(12, 3990)] // nenhuma usada: o valor inteiro
    [InlineData(10, 3324)] // 2 usadas: 39,90 − 2 × 3,33
    [InlineData(1, 327)]
    [InlineData(0, 0)] // todas usadas: 39,90 − 12 × 3,33 ficaria negativo
    public void A_plan_charge_returns_39_90_minus_3_33_per_used_correction(int unused, long amountCents)
    {
        var payment = Payment.StartPlanCharge(Subscription.Request(Guid.NewGuid(), "key", null, Now), "123", Now);

        Assert.Equal(amountCents, RefundRequest.AmountFor(payment, unused));
    }

    [Fact]
    public void A_pending_purchase_is_not_refundable()
    {
        var payment = Payment.StartSingle(Guid.NewGuid(), PaymentMethod.Pix, 1, "key", null, Now);

        Assert.False(payment.RefundableOn(Now));
    }
}
