namespace Web.Bff.Persistence;

/// <summary>A product's name as Catalog published it, joined on read so a line never snapshots one (§10.7).</summary>
public sealed class ProductRow
{
    public Guid ProductId { get; private set; }

    public string Name { get; private set; } = null!;

    public DateTimeOffset PublishedAt { get; private set; }
}
