using Common.Application;

namespace Catalog.Application.Products.WithdrawProduct;

/// <summary>Take one of the caller's own products off sale for good (ADR-074).</summary>
/// <remarks>
/// Keyed rather than declared retry-safe: a repeat after the first succeeded would otherwise answer the rule
/// failure a second withdrawal is, and the caller would read its own success as a refusal (ADR-058).
/// </remarks>
public sealed record WithdrawProductCommand(Guid CommandId, Guid ProductId) : ICommand<Result>, IIdempotentCommand
{
    /// <summary>Declared, never derived from the type name, so a rename cannot change a live key (§8.5).</summary>
    public static string OperationName => "catalog.product.withdraw";
}
