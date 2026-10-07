namespace Catalog.Application.Products.GetProducts;

/// <summary>The listing's closed set of orderings (ADR-073).</summary>
public static class ProductSort
{
    /// <summary>Newest first, by <c>PublishedAt</c> then <c>Id</c>, and the default when none is named.</summary>
    public const string Newest = "newest";

    /// <summary>By <c>Name</c> then <c>Id</c>, ascending in the column's own collation.</summary>
    public const string Name = "name";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Newest, Name };

    /// <summary>The ordering a query asked for, an absent or empty <c>sort</c> being the default.</summary>
    public static string Resolve(string? sort) => string.IsNullOrEmpty(sort) ? Newest : sort;
}
