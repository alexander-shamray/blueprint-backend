namespace Inventory.Domain.Stock;

/// <summary>§5.2's typed identifier for the product a stock row is keyed on.</summary>
public readonly record struct ProductId(Guid Value)
{
    public static ProductId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
