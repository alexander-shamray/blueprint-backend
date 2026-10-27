using Common.Domain;
using Inventory.Domain.Stock;
using Shouldly;
using Xunit;

namespace Inventory.Domain.Tests;

/// <summary>The count guards' wording and edges, and the stamp when the clock equals the row's.</summary>
public class StockItemBoundaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_negative_count_is_refused_with_its_wording()
    {
        StockItem item = StockItem.Rehydrate(ProductId.New(), available: 0, reserved: 0);

        Should.Throw<DomainException>(() => item.SetOnHand(-1, Now))
            .Message.ShouldBe("On-hand stock cannot be negative.");
    }

    [Fact]
    public void A_count_of_zero_is_accepted_on_an_unreserved_row()
    {
        StockItem item = StockItem.Rehydrate(ProductId.New(), available: 5, reserved: 0);

        item.SetOnHand(0, Now);

        item.Available.ShouldBe(0);
    }

    [Fact]
    public void A_count_below_what_is_reserved_is_refused_with_its_wording()
    {
        StockItem item = StockItem.Rehydrate(ProductId.New(), available: 0, reserved: 4);

        Should.Throw<DomainException>(() => item.SetOnHand(3, Now))
            .Message.ShouldBe("On-hand stock cannot be below what is reserved.");
    }

    [Fact]
    public void A_count_equal_to_what_is_reserved_is_accepted_and_leaves_nothing_available()
    {
        StockItem item = StockItem.Rehydrate(ProductId.New(), available: 2, reserved: 4);

        item.SetOnHand(4, Now);

        item.Available.ShouldBe(0);
        item.Reserved.ShouldBe(4);
    }

    [Fact]
    public void A_clock_equal_to_the_row_moves_the_stamp_one_tick()
    {
        StockItem item = StockItem.Rehydrate(ProductId.New(), available: 1, reserved: 0, updatedAt: Now);

        item.SetOnHand(2, Now);

        item.UpdatedAt.ShouldBe(Now.AddTicks(1), "a stamp that did not move would tie the row's last event");
    }
}
