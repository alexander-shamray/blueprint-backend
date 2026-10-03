using Common.Application;
using Common.Contracts.Shipping.V1;
using Notifications.Application.Rendering;

namespace Notifications.Application.Intake;

/// <summary>§3.2's subscription: the saga finalised on despatch (§9.6).</summary>
public sealed class ShipmentDeliveredHandler(IDispatcher dispatcher) : IIntegrationEventHandler<ShipmentDelivered>
{
    public async Task HandleAsync(ShipmentDelivered integrationEvent, CancellationToken ct) =>
        await dispatcher.SendAsync(
            new RecordNotificationCommand(
                integrationEvent.MessageId,
                TemplateKeys.ShipmentDelivered,
                new NotificationParameters
                {
                    OrderId = integrationEvent.OrderId,
                    OccurredAt = integrationEvent.OccurredAt,
                    TrackingNumber = integrationEvent.TrackingNumber
                },
                Order: null),
            ct);
}
