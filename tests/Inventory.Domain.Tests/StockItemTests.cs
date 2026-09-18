using Common.Domain;
using Inventory.Domain.Stock;
using Inventory.Domain.Stock.Events;
using Shouldly;
using Xunit;

namespace Inventory.Domain.Tests;

public class StockItemTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SetOnHand_on_an_empty_row_makes_everything_available_and_raises_the_level()
    {
        ProductId product = ProductId.New();
        StockItem item = StockItem.Rehydrate(product, available: 0, reserved: 0);

        item.SetOnHand(10, Now);

        item.Available.ShouldBe(10);
        item.Reserved.ShouldBe(0);
        StockLevelChangedDomainEvent raised = item.DomainEvents.ShouldHaveSingleItem()
            .ShouldBeOfType<StockLevelChangedDomainEvent>();
        raised.ProductId.ShouldBe(product);
        raised.Available.ShouldBe(10);
        raised.OccurredAt.ShouldBe(Now);
    }

    [Fact]
    public void SetOnHand_keeps_what_is_reserved_and_moves_the_rest()
    {
        StockItem item = StockItem.Rehydrate(ProductId.New(), available: 3, reserved: 4);

        item.SetOnHand(10, Now);

        item.Available.ShouldBe(6, "on hand minus reserved is what can still be promised");
        item.Reserved.ShouldBe(4);
        item.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<StockLevelChangedDomainEvent>()
            .Available.ShouldBe(6);
    }

    [Fact]
    public void SetOnHand_refuses_a_count_below_what_is_reserved()
    {
        StockItem item = StockItem.Rehydrate(ProductId.New(), available: 0, reserved: 4);

        Should.Throw<DomainException>(() => item.SetOnHand(3, Now));
        item.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void SetOnHand_never_stamps_earlier_than_the_row_already_is()
    {
        StockItem item = StockItem.Rehydrate(ProductId.New(), available: 1, reserved: 0, updatedAt: Now.AddHours(1));

        item.SetOnHand(2, Now);

        item.UpdatedAt.ShouldBe(Now.AddHours(1).AddTicks(1), "a clock behind the row moves the stamp one tick, never back");
        item.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<StockLevelChangedDomainEvent>()
            .OccurredAt.ShouldBe(item.UpdatedAt);
    }

    [Fact]
    public void SetOnHand_refuses_a_negative_count()
    {
        Should.Throw<DomainException>(() => StockItem.Rehydrate(ProductId.New(), 0, 0).SetOnHand(-1, Now));
    }
}
