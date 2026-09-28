using Common.Application;
using Shipping.Application.Shipments;
using Shipping.Domain.Shipments;

namespace Shipping.Application.Orders.RecordOrderConfirmed;

/// <summary>
/// Creates the shipment, or finds one and does nothing. The second case is
/// both a redelivery past the inbox and the late half of section 6's first
/// interleaving, where the row already exists as a <c>Voided</c> tombstone —
/// and the two are deliberately indistinguishable here, because the state
/// machine has already decided what each means.
/// </summary>
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
