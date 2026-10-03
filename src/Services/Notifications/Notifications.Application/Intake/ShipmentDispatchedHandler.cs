using Common.Application;
using Common.Contracts.Shipping.V1;
using Notifications.Application.Rendering;

namespace Notifications.Application.Intake;

/// <summary>§3.2's subscription; the tracking number is a carrier's text and is checked before it is stored.</summary>
public sealed class ShipmentDispatchedHandler(IDispatcher dispatcher) : IIntegrationEventHandler<ShipmentDispatched>
{
    public async Task HandleAsync(ShipmentDispatched integrationEvent, CancellationToken ct) =>
        await dispatcher.SendAsync(
            new RecordNotificationCommand(
                integrationEvent.MessageId,
                TemplateKeys.ShipmentDispatched,
                new NotificationParameters
                {
                    OrderId = integrationEvent.OrderId,
                    OccurredAt = integrationEvent.OccurredAt,
                    TrackingNumber = integrationEvent.TrackingNumber
                },
                Order: null),
            ct);
}
