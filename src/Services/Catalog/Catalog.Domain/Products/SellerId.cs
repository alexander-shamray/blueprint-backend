namespace Catalog.Domain.Products;

/// <summary>The person who published a product, the one thing ADR-074's ownership check compares (§11.4).</summary>
/// <remarks>No <c>New()</c>: Catalog never mints a seller, and a factory would invent a subject (§11.4).</remarks>
public readonly record struct SellerId(Guid Value)
{
    public override string ToString() => Value.ToString();
}
