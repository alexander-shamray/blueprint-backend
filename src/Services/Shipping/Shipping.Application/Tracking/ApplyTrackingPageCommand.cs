using Common.Application;
using Shipping.Application.Carrier;
using Shipping.Domain.Shipments;

namespace Shipping.Application.Tracking;

/// <summary>One carrier page, a command so the state and §7.5's outbox rows commit in §6.3's one unit.</summary>
public sealed record ApplyTrackingPageCommand(
    ShipmentId ShipmentId,
    IReadOnlyList<CarrierEvent> Page,
    DateTimeOffset NextPollAt) : ICommand<Result>;
