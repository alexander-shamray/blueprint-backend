using Common.Application;

namespace Catalog.Application.Products.PublishProduct;

/// <summary>Imperative, named for the business intent, immutable (§6.4), and bound directly from the body.</summary>
/// <remarks>§6.4: <c>CommandId</c> without <c>IIdempotentCommand</c> protects nothing.</remarks>
public sealed record PublishProductCommand(
    Guid CommandId,
    string Name,
    string? ThumbnailUrl,
    // Nullable, so an omitted amount is the validator's 400 rather than a bound 0 and a free product.
    decimal? Amount,
    string Currency) : ICommand<Result<Guid>>, IIdempotentCommand
{
    /// <summary>Declared, never derived from the type name, so a rename cannot change a live key (§8.5).</summary>
    public static string OperationName => "catalog.product.publish";
}
