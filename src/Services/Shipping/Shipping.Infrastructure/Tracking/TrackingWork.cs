namespace Shipping.Infrastructure.Tracking;

/// <summary>One leased row; no address is projected, so none reaches this worker's log (§11.7).</summary>
public sealed record TrackingWork(
    Guid Id,
    Guid OrderId,
    string CarrierReference,
    int PollAttempts,
    DateTimeOffset CreatedAt);
