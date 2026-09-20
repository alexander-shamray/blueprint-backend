using Common.Application;

namespace Catalog.Application.Products.PublishProduct;

/// <summary>
/// Imperative, named for the business intent, immutable (§6.4). Bound directly
/// from the request body: a separate request record earns its place when the
/// wire shape and the command diverge, and here they are identical primitives.
/// </summary>
/// <remarks>
/// <c>CommandId</c> and <c>IIdempotentCommand</c> belong together: §6.4 warns
/// that the field without the interface is unprotected, so a client reading
/// the field would take a retry to be safe while nothing claimed a key.
/// </remarks>
public sealed record PublishProductCommand(
    Guid CommandId,
    string Name,
    string? ThumbnailUrl,
    // Nullable because a bare decimal cannot say "absent": an omitted amount
    // would bind as 0 and publish a free product indistinguishable from a
    // deliberate one. The validator's NotNull turns the omission into the
    // same field-keyed 400 every other bad field gets — a JsonRequired
    // attribute was tried first and surfaces as a 500 through the binder.
    // The reference members need no such dance: omission binds them null.
    decimal? Amount,
    string Currency) : ICommand<Result<Guid>>, IIdempotentCommand
{
    /// <summary>
    /// Declared, never derived from the type name — a rename must not be able
    /// to change a live key (§8.5). Spelled in the domain's vocabulary rather
    /// than the CLR's, so that copying the type name back in reads as the
    /// mistake it is.
    /// </summary>
    public static string OperationName => "catalog.product.publish";
}
