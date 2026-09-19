using Common.Application;
using Common.Contracts.Shipping.V1;
using Inventory.Application.Reservations.Fulfil;

namespace Inventory.Application.Reservations.Integration;

public sealed class ShipmentDispatchedHandler(IDispatcher dispatcher) : IIntegrationEventHandler<ShipmentDispatched>
{
    public async Task HandleAsync(ShipmentDispatched integrationEvent, CancellationToken ct)
    {
        await dispatcher.SendAsync(new FulfilReservationCommand(integrationEvent.OrderId), ct);
    }
}
