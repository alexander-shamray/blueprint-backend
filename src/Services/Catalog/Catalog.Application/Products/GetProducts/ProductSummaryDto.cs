namespace Catalog.Application.Products.GetProducts;

/// <summary>
/// Exactly the shape this listing needs — no generic DTO reused across
/// endpoints (§6.5). The price rides as its two column values: a query
/// bypasses the domain model, and rehydrating <c>Money</c> to serialise it
/// back out would be the loop §6.5 exists to remove. The level is Inventory's
/// (§3.2), as Catalog last projected it, and it is null rather than 0 for a
/// product Inventory has never reported: unknown and none are different
/// facts to a screen. Last, so Dapper's positional mapping of the columns
/// before it does not move.
/// </summary>
public sealed record ProductSummaryDto(
    Guid ProductId,
    string Name,
    string? ThumbnailUrl,
    decimal Amount,
    string Currency,
    DateTimeOffset PublishedAt,
    int? QuantityAvailable);
