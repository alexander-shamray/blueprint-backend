namespace Catalog.Application.Products.GetProducts;

/// <summary>
/// Exactly the shape this listing needs (§6.5), with the price as its two column values and Inventory's level
/// (§3.2) null where never reported, last so Dapper's positional mapping of the others does not move.
/// </summary>
public sealed record ProductSummaryDto(
    Guid ProductId,
    string Name,
    string? ThumbnailUrl,
    decimal Amount,
    string Currency,
    DateTimeOffset PublishedAt,
    int? QuantityAvailable);
