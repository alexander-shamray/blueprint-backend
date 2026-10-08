namespace Shipping.Infrastructure.Fulfilment;

/// <summary>One leased row, with the two instants the give-up age is measured from (ADR-054).</summary>
/// <remarks>The trace pair is the write that asked for this pass, which the pass restores as its parent (§9.4).</remarks>
public sealed record FulfilmentWork(
    Guid Id,
    Guid OrderId,
    string Status,
    string? CarrierReference,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CancellationRequestedAt,
    string? TraceParent,
    string? TraceState,
    DateTimeOffset LockedUntil);
