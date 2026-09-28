using Common.Application;
using Microsoft.Extensions.Logging;
using Shipping.Application.Shipments;
using Shipping.Domain.Shipments;

namespace Shipping.Application.Orders.RecordOrderCancelled;

public sealed class VoidShipmentHandler(
    IShipmentRepository shipments, TimeProvider clock, ILogger<VoidShipmentHandler> log)
    : ICommandHandler<VoidShipmentCommand, Result>
{
    // A superseded arrival (spec, section 5) — a second cancellation, or one
    // of a shipment already despatched — is logged and returned rather than
    // thrown. The shipment id and the order id only, never an address
    // (§11.7). Compiled once (CA1848, ADR-019).
    private static readonly Action<ILogger, Guid, Guid, Exception?> Superseded =
        LoggerMessage.Define<Guid, Guid>(
            LogLevel.Information,
            new EventId(1, nameof(Superseded)),
            "Cancellation of shipment {ShipmentId} for order {OrderId} moved nothing; already superseded.");

    public async Task<Result> HandleAsync(VoidShipmentCommand command, CancellationToken ct)
    {
        OrderId order = new(command.OrderId);
        DateTimeOffset now = clock.GetUtcNow();

        Shipment? shipment = await shipments.GetByOrderAsync(order, ct);

        if (shipment is null)
        {
            // The tombstone (spec, section 8). A row rather than nothing,
            // because the late confirmation has to find something: with no row
            // it would create a Pending shipment for a cancelled order and a
            // worker would book it.
            shipment = Shipment.For(ShipmentId.New(), order, now);
            shipment.Cancel(now);
            shipments.Add(shipment);

            return Result.Success();
        }

        if (!shipment.Cancel(now))
            Superseded(log, shipment.Id.Value, order.Value, null);

        return Result.Success();
    }
}
