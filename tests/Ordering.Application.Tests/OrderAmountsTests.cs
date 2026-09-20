using System.Globalization;
using Shouldly;
using Xunit;

namespace Ordering.Application.Tests;

/// <summary>
/// The ceiling is only worth enforcing if it is the money column's own, so
/// what is asserted here is the derivation rather than the number.
/// </summary>
public sealed class OrderAmountsTests
{
    [Fact]
    public void The_ceiling_is_the_first_amount_the_money_column_cannot_hold()
    {
        // A decimal(Precision, Scale) column holds Precision digits with Scale
        // of them after the point, so the ceiling needs one digit more than
        // that and the amount one whole unit below it needs exactly that many.
        // Written as digit counts because restating the number here would make
        // this test agree with OrderAmounts by copying it.
        (IntegerDigitsOf(OrderAmounts.Ceiling) + OrderAmounts.Scale)
            .ShouldBe(OrderAmounts.Precision + 1);

        (IntegerDigitsOf(OrderAmounts.Ceiling - 1m) + OrderAmounts.Scale)
            .ShouldBe(OrderAmounts.Precision);
    }

    [Fact]
    public void The_scale_leaves_room_for_the_places_money_is_rounded_to()
    {
        // Money.Of rounds to two places, so a scale below that would round an
        // amount the domain accepted on its way into the column.
        OrderAmounts.Scale.ShouldBeGreaterThanOrEqualTo(2);
    }

    private static int IntegerDigitsOf(decimal value) =>
        decimal.Truncate(value).ToString(CultureInfo.InvariantCulture).Length;
}
