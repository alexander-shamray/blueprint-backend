using Common.Application;
using Shipping.Domain.Shipments;

namespace Shipping.Application.Tracking;

/// <summary>
/// A leased shipment past ADR-054's tracking age. A command for
/// <see cref="ApplyTrackingPageCommand"/>'s reason: §6.3's behaviour opens the
/// unit of work the state and the released lease commit in.
/// </summary>
public sealed record AbandonShipmentCommand(ShipmentId ShipmentId) : ICommand<Result>;
