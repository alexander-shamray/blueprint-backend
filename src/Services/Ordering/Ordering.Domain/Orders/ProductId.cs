namespace Ordering.Domain.Orders;

/// <summary>A product as Ordering knows it: Catalog owns the aggregate (ADR-002).</summary>
/// <remarks>Not Catalog's type, which would cross a boundary (§4.3); it arrives as a primitive (§9.1).</remarks>
public readonly record struct ProductId(Guid Value)
{
    public static ProductId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
