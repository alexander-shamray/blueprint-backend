using Common.Application;
using Common.Contracts.Ordering.V1;
using Notifications.Application.Rendering;

namespace Notifications.Application.Intake;

/// <summary>§3.2's subscription, and the one that writes ADR-049's deciding fact onto the order record.</summary>
public sealed class OrderCancelledHandler(IDispatcher dispatcher) : IIntegrationEventHandler<OrderCancelled>
{
    public async Task HandleAsync(OrderCancelled integrationEvent, CancellationToken ct) =>
        await dispatcher.SendAsync(
            new RecordNotificationCommand(
                integrationEvent.MessageId,
                integrationEvent.CorrelationId,
                TemplateKeys.OrderCancelled,
                new NotificationParameters
                {
                    OrderId = integrationEvent.OrderId,
                    OccurredAt = integrationEvent.OccurredAt,
                    CancelReason = integrationEvent.Reason
                },
                new OrderFact(
                    integrationEvent.CustomerId,
                    new OrderCancellation(
                        integrationEvent.Reason,
                        integrationEvent.Origin,
                        integrationEvent.OccurredAt))),
            ct);
}
