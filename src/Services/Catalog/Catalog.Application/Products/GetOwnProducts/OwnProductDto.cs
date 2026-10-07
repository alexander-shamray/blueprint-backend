namespace Catalog.Application.Products.GetOwnProducts;

/// <summary>
/// The listing's row plus <c>WithdrawnAt</c>, since a seller's own list keeps what the public reads hide
/// (ADR-074); Inventory's level stays last so Dapper's positional mapping holds.
/// </summary>
public sealed record OwnProductDto(
    Guid ProductId,
    string Name,
    string? ThumbnailUrl,
    decimal Amount,
    string Currency,
    DateTimeOffset PublishedAt,
    DateTimeOffset? WithdrawnAt,
    int? QuantityAvailable);
