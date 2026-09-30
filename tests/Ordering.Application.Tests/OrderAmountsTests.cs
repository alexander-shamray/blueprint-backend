using System.Globalization;
using Shouldly;
using Xunit;

namespace Ordering.Application.Tests;

/// <summary>Asserts the ceiling's derivation from the money column rather than its number.</summary>
public sealed class OrderAmountsTests
{
    [Fact]
    public void The_ceiling_is_the_first_amount_the_money_column_cannot_hold()
    {
        // A decimal(Precision, Scale) column holds Precision digits, so the ceiling needs one more.
        (IntegerDigitsOf(OrderAmounts.Ceiling) + OrderAmounts.Scale)
            .ShouldBe(OrderAmounts.Precision + 1);

        (IntegerDigitsOf(OrderAmounts.Ceiling - 1m) + OrderAmounts.Scale)
            .ShouldBe(OrderAmounts.Precision);
    }

    [Fact]
    public void The_scale_leaves_room_for_the_places_money_is_rounded_to()
    {
        // Money.Of rounds to two places, so a smaller scale would round an accepted amount again.
        OrderAmounts.Scale.ShouldBeGreaterThanOrEqualTo(2);
    }

    private static int IntegerDigitsOf(decimal value) =>
        decimal.Truncate(value).ToString(CultureInfo.InvariantCulture).Length;
}
