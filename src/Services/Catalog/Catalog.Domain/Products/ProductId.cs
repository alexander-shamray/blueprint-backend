namespace Catalog.Domain.Products;

/// <summary>§5.2's strongly typed identifier, so <c>GetProduct(categoryId)</c> is a compile error.</summary>
/// <remarks>Version 7 for the creation time it carries, not insert locality, which §5.2's trap explains.</remarks>
public readonly record struct ProductId(Guid Value)
{
    public static ProductId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
