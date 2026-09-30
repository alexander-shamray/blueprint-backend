using Common.Domain;
using Inventory.Domain.Stock.Events;

namespace Inventory.Domain.Stock;

/// <summary>One count per product, loaded by the admin path only; reservations move it by §7.3's statement.</summary>
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

        // Monotonic per row, as the ledger's stamp is (§7.3); the row is locked from the load to the commit.
        UpdatedAt = now > UpdatedAt ? now : UpdatedAt.AddTicks(1);
        Raise(new StockLevelChangedDomainEvent(Id, Available, UpdatedAt));
    }
}
