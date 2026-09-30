using Common.Domain;
using Ordering.Domain.Orders;
using Shouldly;
using Xunit;

namespace Ordering.Domain.Tests;

/// <summary>
/// <see cref="PaymentReference"/> and <see cref="TrackingNumber"/>, whose guards are the same pair, presence and
/// length, and nothing about a format somebody else mints.
/// </summary>
/// <remarks>Lengths are <c>MaxLength</c> arithmetic, because the constant is the column width (§7.2).</remarks>
public class CarrierAndPaymentReferenceTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_payment_reference_cannot_be_blank(string value)
    {
        Should.Throw<DomainException>(() => PaymentReference.Of(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_tracking_number_cannot_be_blank(string value)
    {
        Should.Throw<DomainException>(() => TrackingNumber.Of(value));
    }

    [Fact]
    public void A_payment_reference_of_exactly_the_column_width_is_accepted()
    {
        string value = new('p', PaymentReference.MaxLength);

        PaymentReference.Of(value).Value.ShouldBe(value);
    }

    [Fact]
    public void A_tracking_number_of_exactly_the_column_width_is_accepted()
    {
        string value = new('t', TrackingNumber.MaxLength);

        TrackingNumber.Of(value).Value.ShouldBe(value);
    }

    [Fact]
    public void A_payment_reference_one_character_past_the_column_width_is_refused()
    {
        Should.Throw<DomainException>(() =>
            PaymentReference.Of(new string('p', PaymentReference.MaxLength + 1)));
    }

    [Fact]
    public void A_tracking_number_one_character_past_the_column_width_is_refused()
    {
        Should.Throw<DomainException>(() =>
            TrackingNumber.Of(new string('t', TrackingNumber.MaxLength + 1)));
    }

    [Fact]
    public void Both_trim_what_they_accept()
    {
        PaymentReference.Of(" pay_123 ").Value.ShouldBe("pay_123");
        TrackingNumber.Of(" TRK-1 ").Value.ShouldBe("TRK-1");
    }
}
