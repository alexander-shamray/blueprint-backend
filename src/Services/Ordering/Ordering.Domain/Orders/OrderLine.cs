using Common.Domain;
using Ordering.Domain.Common;

namespace Ordering.Domain.Orders;

/// <summary>A line on an order: an entity reached only through <see cref="Order"/>, its invariants' owner.</summary>
/// <remarks>Its own key type, never <see cref="OrderId"/>, so a line cannot equal its order (§5.5).</remarks>
public sealed class OrderLine : Entity<OrderLineId>
{
    public ProductId ProductId { get; private set; }
    public int Quantity { get; private set; }
    public Money UnitPrice { get; private set; }

    public Money LineTotal => UnitPrice * Quantity;

    // EF Core materialisation only (§5.4).
    private OrderLine() { }

    private OrderLine(OrderLineId id, ProductId productId, int quantity, Money unitPrice)
    {
        Id = id;
        ProductId = productId;
        Quantity = quantity;
        UnitPrice = unitPrice;
    }

    /// <summary>Internal: only <see cref="Order"/> creates a line; the quantity guard is on <c>AddLine</c>.</summary>
    internal static OrderLine For(ProductId productId, int quantity, Money unitPrice) =>
        new(OrderLineId.New(), productId, quantity, unitPrice);

    internal void IncreaseQuantity(int by)
    {
        if (by <= 0)
            throw new DomainException("Quantity must be positive.");

        Quantity += by;
    }
}
