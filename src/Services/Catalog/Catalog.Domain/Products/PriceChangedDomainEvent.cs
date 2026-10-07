using Catalog.Domain.Common;
using Common.Domain;

namespace Catalog.Domain.Products;

/// <summary>
/// Raised by <see cref="Product.ChangePrice"/>, carrying what the <c>PriceChanged</c> contract takes from the
/// domain (§5.5); the mapper flattens it and supplies the rest of the envelope (§9.3).
/// </summary>
public sealed record PriceChangedDomainEvent(
    ProductId ProductId,
    Money Price,
    DateTimeOffset OccurredAt) : IDomainEvent;
