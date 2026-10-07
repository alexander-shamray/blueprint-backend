namespace Shipping.Infrastructure.Tracking;

/// <summary>One leased row; no address is projected, so none reaches this worker's log (§11.7).</summary>
/// <remarks>The trace pair is the write that recorded the shipment, which each poll links to (§9.4).</remarks>
public sealed record TrackingWork(
    Guid Id,
    Guid OrderId,
    string CarrierReference,
    int PollAttempts,
    DateTimeOffset CreatedAt,
    string? TraceParent,
    string? TraceState);
