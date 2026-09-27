using Shipping.Domain.Shipments;

namespace Shipping.Application.Carrier;

/// <summary>
/// One translated fact from the carrier's feed. <see cref="CarrierEventId"/>
/// is the carrier's own id and half of <c>TrackingEvent</c>'s key, which is
/// what makes a repeated page free (spec, section 5).
/// </summary>
/// <remarks>
/// There is no link here on purpose (spec, section 9): the carrier's own
/// URLs are never stored, and whoever renders one rebuilds it from a host
/// its own configuration allow-lists.
/// </remarks>
public sealed record CarrierEvent(string CarrierEventId, TrackingStatus Status, DateTimeOffset OccurredAt);
