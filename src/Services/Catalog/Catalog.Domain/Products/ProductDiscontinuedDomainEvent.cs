using Common.Domain;

namespace Catalog.Domain.Products;

/// <summary>
/// Raised by <see cref="Product.Withdraw"/>, carrying what the <c>ProductDiscontinued</c> contract takes from the
/// domain (§5.5); the mapper flattens it and supplies the rest of the envelope (§9.3).
/// </summary>
public sealed record ProductDiscontinuedDomainEvent(
    ProductId ProductId,
    DateTimeOffset OccurredAt) : IDomainEvent;
