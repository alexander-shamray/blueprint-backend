namespace Common.Contracts.Ordering.V1;

/// <summary>
/// The bounds an order's lines satisfy, enforced by Ordering on the command and
/// by the BFF on the quote, so a quote never prices a basket the order refuses.
/// </summary>
/// <remarks>
/// Here because §4.3 lets only this assembly cross a service boundary: a
/// constant in Ordering would make the BFF read a service's internals, and one
/// in the BFF would make Ordering depend on a host. Not a message type, but a
/// fact about an order's shape that crosses a boundary.
/// </remarks>
public static class OrderLimits
{
    /// <summary>
    /// The fewest of a product a line may carry. A line for none of something
    /// is a line the caller meant to remove, and reading it as an order for
    /// zero is a basket the customer did not assemble.
    /// </summary>
    public const int MinQuantity = 1;

    /// <summary>
    /// The most of one product a basket may carry — a business bound, past
    /// which the basket is a wholesale order.
    /// </summary>
    /// <remarks>
    /// Of a product, not of a line: a repeated product merges, so a validator
    /// sums by product before comparing, or two full lines would order twice
    /// this (ADR-045).
    /// </remarks>
    public const int MaxQuantity = 999;

    /// <summary>
    /// The most lines one order — or one quote for it — may carry.
    /// </summary>
    /// <remarks>
    /// <c>ProjectedPriceReader</c> binds one SQL parameter per product id plus
    /// <c>@Currency</c>, and SQL Server stops at 2,100, so without a ceiling a
    /// large enough well-formed request is a 500. A hundred is a business bound
    /// well inside that; raising it towards the limit is the moment to batch
    /// the query.
    /// </remarks>
    public const int MaxLines = 100;
}
