namespace Common.Contracts.Catalog.V1;

/// <summary>
/// A product's price moved (§3.2). Ordering projects it into
/// <c>ordering.ProductPrices</c> and reads that on the write path (§6.4), which
/// is why the amount travels rather than an instruction to fetch one.
/// </summary>
/// <remarks>
/// <c>Amount</c> and <c>Currency</c> rather than a <c>Money</c>: a contract
/// carries primitives (§9.1), and the currency travels beside the amount for
/// the same reason §9.6 gives for <c>AuthorisePayment</c> carrying one.
/// </remarks>
public sealed record PriceChanged : IIntegrationEvent
{
    public required Guid MessageId { get; init; }

    public required Guid CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid ProductId { get; init; }

    public required decimal Amount { get; init; }

    public required string Currency { get; init; }
}
