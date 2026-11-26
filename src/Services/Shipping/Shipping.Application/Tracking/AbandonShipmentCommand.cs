using Common.Application;
using Shipping.Domain.Shipments;

namespace Shipping.Application.Tracking;

/// <summary>A leased shipment past ADR-054's tracking age, a command so §6.3 opens the unit of work.</summary>
/// <remarks><c>Lease</c> is the claim's; <see cref="Shipment.PollApplied"/> releases it only while held.</remarks>
public sealed record AbandonShipmentCommand(ShipmentId ShipmentId, DateTimeOffset Lease) : ICommand<Result>;
