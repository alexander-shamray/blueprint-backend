using Common.Application;
using Microsoft.Extensions.Logging;
using Shipping.Application.Shipments;
using Shipping.Domain.Shipments;

namespace Shipping.Application.Orders.RecordOrderCancelled;

public sealed class VoidShipmentHandler(
    IShipmentRepository shipments, TimeProvider clock, ILogger<VoidShipmentHandler> log)
    : ICommandHandler<VoidShipmentCommand, Result>
{
    // A superseded arrival is logged, not thrown; ids only, never an address (§11.7). CA1848 (ADR-019).
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
            // A tombstone, so a late confirmation finds a row rather than creating a shipment a worker would book.
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
