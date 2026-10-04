namespace Web.Bff.Persistence;

/// <summary>One line an order was placed with, keyed by its position in the event's list (ADR-051).</summary>
public sealed class OrderLineRow
{
    public Guid OrderId { get; private set; }

    public int LineNumber { get; private set; }

    public Guid ProductId { get; private set; }

    public int Quantity { get; private set; }

    public decimal UnitPrice { get; private set; }
}
