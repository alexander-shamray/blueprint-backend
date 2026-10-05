using Common.Application;
using Common.Contracts.Payments.V1;
using Notifications.Application.Rendering;

namespace Notifications.Application.Intake;

/// <summary>§3.2's subscription; <c>Reason</c> is never read, since ADR-049 forbids branching on it.</summary>
public sealed class PaymentDeclinedHandler(IDispatcher dispatcher) : IIntegrationEventHandler<PaymentDeclined>
{
    public async Task HandleAsync(PaymentDeclined integrationEvent, CancellationToken ct) =>
        await dispatcher.SendAsync(
            new RecordNotificationCommand(
                integrationEvent.MessageId,
                integrationEvent.CorrelationId,
                TemplateKeys.PaymentDeclined,
                new NotificationParameters
                {
                    OrderId = integrationEvent.OrderId,
                    OccurredAt = integrationEvent.OccurredAt
                },
                Order: null),
            ct);
}
