using Common.Application;
using Shipping.Application.Shipments;
using Shipping.Domain.Shipments;

namespace Shipping.Application.Orders.RecordOrderConfirmed;

/// <summary>Creates the shipment, or finds one, perhaps a <c>Voided</c> tombstone, and does nothing.</summary>
public sealed class CreateShipmentHandler(IShipmentRepository shipments, TimeProvider clock)
    : ICommandHandler<CreateShipmentCommand, Result>
{
    public async Task<Result> HandleAsync(CreateShipmentCommand command, CancellationToken ct)
    {
        OrderId order = new(command.OrderId);

        if (await shipments.GetByOrderAsync(order, ct) is not null)
            return Result.Success();

        shipments.Add(Shipment.For(ShipmentId.New(), order, clock.GetUtcNow()));

        return Result.Success();
    }
}
