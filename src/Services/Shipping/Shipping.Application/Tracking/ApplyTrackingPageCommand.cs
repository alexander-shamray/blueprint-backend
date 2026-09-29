using Common.Application;
using Shipping.Application.Carrier;
using Shipping.Domain.Shipments;

namespace Shipping.Application.Tracking;

/// <summary>
/// One page the carrier answered, for one shipment. A command rather than a
/// method the worker calls, so §6.3's <c>TransactionBehavior</c> opens the unit
/// of work and §7.5's dispatcher stages the outbox rows inside it — the two
/// integration events and the state they describe commit together or not at
/// all.
/// </summary>
public sealed record ApplyTrackingPageCommand(
    ShipmentId ShipmentId,
    IReadOnlyList<CarrierEvent> Page,
    DateTimeOffset NextPollAt) : ICommand<Result>;
