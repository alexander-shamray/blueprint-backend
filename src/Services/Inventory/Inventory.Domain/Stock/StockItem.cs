using Common.Domain;
using Inventory.Domain.Stock.Events;

namespace Inventory.Domain.Stock;

/// <summary>
/// One count per product (§3.2, §7.3). The aggregate for the admin path only:
/// the reservation path writes these columns by §7.3's statement and never
/// loads this type.
/// </summary>
public sealed class StockItem : AggregateRoot<ProductId>
{
    public int Available { get; private set; }
    public int Reserved { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private StockItem() { }

    private StockItem(ProductId id, int available, int reserved, DateTimeOffset now)
    {
        Id = id;
        Available = available;
        Reserved = reserved;
        UpdatedAt = now;
    }

    internal static StockItem Rehydrate(
        ProductId product,
        int available,
        int reserved,
        DateTimeOffset? updatedAt = null) =>
        new(product, available, reserved, updatedAt ?? DateTimeOffset.MinValue);

    public void SetOnHand(int onHand, DateTimeOffset now)
    {
        if (onHand < 0)
            throw new DomainException("On-hand stock cannot be negative.");

        // A stock-take cannot make the warehouse hold less than it has promised.
        if (onHand < Reserved)
            throw new DomainException("On-hand stock cannot be below what is reserved.");

        Available = onHand - Reserved;

        // Monotonic per product, from this side as the ledger's statement is
        // from its side: the level's instant is the clock when the clock is
        // ahead of the row and one tick past the row otherwise, so Catalog's
        // watermark never keeps an older level for a newer stamp whatever the
        // two clocks do. The row is locked from the load to the commit, so
        // no ledger stamp lands between the two.
        UpdatedAt = now > UpdatedAt ? now : UpdatedAt.AddTicks(1);
        Raise(new StockLevelChangedDomainEvent(Id, Available, UpdatedAt));
    }
}
