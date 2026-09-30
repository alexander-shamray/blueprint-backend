using Shipping.Domain.Shipments;

namespace Shipping.Application.Carrier;

/// <summary>One translated fact from the carrier's feed, with no link: a carrier's URL is never stored.</summary>
public sealed record CarrierEvent(string CarrierEventId, TrackingStatus Status, DateTimeOffset OccurredAt);
