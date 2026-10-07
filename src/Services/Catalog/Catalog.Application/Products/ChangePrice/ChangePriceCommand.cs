using Common.Application;

namespace Catalog.Application.Products.ChangePrice;

/// <summary>Set a product's price to an absolute amount, so a repeat sets what the first set (ADR-058).</summary>
/// <remarks>Not <c>IIdempotentCommand</c>: the same price again changes nothing and raises nothing (§5.4).</remarks>
public sealed record ChangePriceCommand(
    Guid ProductId,
    // Nullable, so an omitted amount is the validator's 400 rather than a bound 0 and a free product.
    decimal? Amount,
    string Currency) : ICommand<Result>;
