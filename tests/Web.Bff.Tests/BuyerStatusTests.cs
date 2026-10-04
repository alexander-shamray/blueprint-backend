using Shouldly;
using Web.Bff.Orders;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>§10.7's rank over every subset of the five steps: the highest absorbed, never the latest.</summary>
public sealed class BuyerStatusTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The rank as a list, highest first: a second spelling of the order for the oracle below.</summary>
    private static readonly string[] Rank =
    [
        BuyerStatuses.Delivered,
        BuyerStatuses.Cancelled,
        BuyerStatuses.Dispatched,
        BuyerStatuses.Confirmed,
        BuyerStatuses.Placed
    ];

    public static TheoryData<int> EverySubset()
    {
        TheoryData<int> subsets = [];
        for (int mask = 0; mask < 1 << 5; mask++)
            subsets.Add(mask);
        return subsets;
    }

    [Theory]
    [MemberData(nameof(EverySubset))]
    public void The_status_is_the_highest_step_present(int mask)
    {
        // Bit i set means Rank[i]'s step was absorbed; a higher step gets an earlier instant, so latest is not highest.
        DateTimeOffset? Step(int bit) => (mask & (1 << bit)) != 0 ? At.AddMinutes(bit) : null;

        OrderSteps steps = new(
            PlacedAt: Step(4),
            ConfirmedAt: Step(3),
            DispatchedAt: Step(2),
            DeliveredAt: Step(0),
            CancelledAt: Step(1),
            CancelOutcome: Step(1) is null ? null : BuyerStatuses.Cancelled);

        string? expected = Enumerable.Range(0, 5).Where(bit => Step(bit) is not null).Select(bit => Rank[bit])
            .FirstOrDefault();

        BuyerStatus.Of(steps).ShouldBe(expected);
    }

    [Theory]
    [InlineData(BuyerStatuses.Cancelled)]
    [InlineData(BuyerStatuses.OutOfStock)]
    [InlineData(BuyerStatuses.Declined)]
    public void A_cancellation_outranks_a_despatch_and_reads_as_its_stored_member(string member) =>
        BuyerStatus.Of(new OrderSteps(At, At, At.AddHours(1), null, At.AddMinutes(30), member)).ShouldBe(
            member,
            "Order.Cancel refuses a shipped order, so a cancellation on the wire preceded the despatch (§10.7)");

    [Fact]
    public void Delivery_outranks_a_cancellation() =>
        BuyerStatus.Of(new OrderSteps(At, At, At, At.AddDays(1), At, BuyerStatuses.Declined))
            .ShouldBe(BuyerStatuses.Delivered);

    [Fact]
    public void A_cancellation_with_no_stored_member_reads_as_the_member_that_claims_least() =>
        BuyerStatus.Of(new OrderSteps(null, null, null, null, At, null)).ShouldBe(BuyerStatuses.Cancelled);

    [Fact]
    public void A_row_no_step_has_reached_has_no_status() =>
        BuyerStatus.Of(new OrderSteps(null, null, null, null, null, null)).ShouldBeNull();
}
