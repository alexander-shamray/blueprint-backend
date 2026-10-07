using System.Globalization;
using Common.Application;

namespace Catalog.Application.Products.GetProducts;

/// <summary>
/// The listing's opaque cursor (ADR-016): the ordering and the search it was minted under, then the last row's
/// sort key and its <c>Id</c> tiebreaker, so a cursor reused under another query is refused rather than read as a
/// position in an ordering it does not belong to (ADR-073).
/// </summary>
public sealed record ProductCursor(string Sort, string? Search, DateTimeOffset? PublishedAt, string? Name, Guid Id)
{
    public static ProductCursor ForNewest(string? search, DateTimeOffset publishedAt, Guid id) =>
        new(ProductSort.Newest, search, publishedAt.ToUniversalTime(), null, id);

    public static ProductCursor ForName(string? search, string name, Guid id) =>
        new(ProductSort.Name, search, null, name, id);

    public bool Matches(string sort, string? search) =>
        string.Equals(Sort, sort, StringComparison.Ordinal) && string.Equals(Search, search, StringComparison.Ordinal);

    public string Encode()
    {
        string search = Search ?? "";
        string key = PublishedAt is { } at ? at.UtcTicks.ToString(CultureInfo.InvariantCulture) : Name!;

        // Both free-text fields may hold the separator, so the search is length-prefixed and the key is the rest.
        return Cursor.Wrap(
            string.Create(CultureInfo.InvariantCulture, $"{Sort}:{Id:N}:{search.Length}:{search}{key}"));
    }

    /// <summary>Null for null and for anything unreadable, so an edited cursor gets the first page (§6.5).</summary>
    public static ProductCursor? Decode(string? cursor)
    {
        string[]? parts = Cursor.Unwrap(cursor)?.Split(':', 4);
        if (parts is not { Length: 4 } ||
            !Guid.TryParseExact(parts[1], "N", out Guid id) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int length) ||
            length > parts[3].Length)
        {
            return null;
        }

        string? search = length == 0 ? null : parts[3][..length];
        string key = parts[3][length..];

        return parts[0] switch
        {
            ProductSort.Newest when ReadInstant(key) is { } at => new ProductCursor(parts[0], search, at, null, id),
            ProductSort.Name when key.Length > 0 => new ProductCursor(parts[0], search, null, key, id),
            _ => null
        };
    }

    private static DateTimeOffset? ReadInstant(string key) =>
        long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out long ticks) &&
        ticks <= DateTime.MaxValue.Ticks
            ? new DateTimeOffset(ticks, TimeSpan.Zero)
            : null;
}
