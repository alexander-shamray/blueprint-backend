namespace Shipping.Infrastructure.Fulfilment;

/// <summary>One leased row, with the two instants the give-up age is measured from (ADR-054).</summary>
public sealed record FulfilmentWork(
    Guid Id,
    Guid OrderId,
    string Status,
    string? CarrierReference,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CancellationRequestedAt);
