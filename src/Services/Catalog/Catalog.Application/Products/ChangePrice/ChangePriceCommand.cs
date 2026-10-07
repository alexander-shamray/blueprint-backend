using Common.Application;

namespace Catalog.Application.Products.ChangePrice;

/// <summary>Set a product's price to an absolute amount; the same price again raises nothing (§5.4).</summary>
/// <remarks>
/// Keyed rather than convergent: a stale repeat arriving after a later change would put the older price back, and
/// orders are priced at it (ADR-058).
/// </remarks>
public sealed record ChangePriceCommand(
    Guid CommandId,
    Guid ProductId,
    // Nullable, so an omitted amount is the validator's 400 rather than a bound 0 and a free product.
    decimal? Amount,
    string Currency) : ICommand<Result>, IIdempotentCommand
{
    /// <summary>Declared, never derived from the type name, so a rename cannot change a live key (§8.5).</summary>
    public static string OperationName => "catalog.product.change-price";
}
