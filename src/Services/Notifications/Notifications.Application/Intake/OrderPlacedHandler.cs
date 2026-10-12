using Common.Application;
using Common.Contracts.Ordering.V1;
using Notifications.Application.Rendering;

namespace Notifications.Application.Intake;

/// <summary>§3.2's subscription; the order record it may create names the customer four events wait on.</summary>
public sealed class OrderPlacedHandler(ErasedIntake intake) : IIntegrationEventHandler<OrderPlaced>
{
    public async Task HandleAsync(OrderPlaced integrationEvent, CancellationToken ct) =>
        await intake.RecordAsync(
            new RecordNotificationCommand(
                integrationEvent.MessageId,
                integrationEvent.CorrelationId,
                TemplateKeys.OrderPlaced,
                new NotificationParameters
                {
                    OrderId = integrationEvent.OrderId,
                    OccurredAt = integrationEvent.OccurredAt,
                    Amount = integrationEvent.TotalAmount,
                    Currency = integrationEvent.Currency
                },
                new OrderFact(integrationEvent.CustomerId, Cancellation: null, integrationEvent.ErasedUnder)),
            ct);
}
