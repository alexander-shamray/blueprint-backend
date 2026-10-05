using Common.Application;
using Common.Contracts.Payments.V1;
using Notifications.Application.Rendering;

namespace Notifications.Application.Intake;

/// <summary>§3.2's subscription; it names an order and no customer, so the row waits on the order record.</summary>
public sealed class PaymentRefundedHandler(IDispatcher dispatcher) : IIntegrationEventHandler<PaymentRefunded>
{
    public async Task HandleAsync(PaymentRefunded integrationEvent, CancellationToken ct) =>
        await dispatcher.SendAsync(
            new RecordNotificationCommand(
                integrationEvent.MessageId,
                integrationEvent.CorrelationId,
                TemplateKeys.PaymentRefunded,
                new NotificationParameters
                {
                    OrderId = integrationEvent.OrderId,
                    OccurredAt = integrationEvent.OccurredAt,
                    Amount = integrationEvent.Amount,
                    Currency = integrationEvent.Currency
                },
                Order: null),
            ct);
}
