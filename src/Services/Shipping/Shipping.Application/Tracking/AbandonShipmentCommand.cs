using Common.Application;
using Shipping.Domain.Shipments;

namespace Shipping.Application.Tracking;

/// <summary>A leased shipment past ADR-054's tracking age, a command so §6.3 opens the unit of work.</summary>
public sealed record AbandonShipmentCommand(ShipmentId ShipmentId) : ICommand<Result>;
