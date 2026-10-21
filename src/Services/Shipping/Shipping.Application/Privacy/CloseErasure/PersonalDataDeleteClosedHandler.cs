using Common.Application;
using Common.Contracts.Privacy.V1;

namespace Shipping.Application.Privacy.CloseErasure;

/// <summary>Takes Privacy's close of a request onto Shipping's own audit row (ADR-139).</summary>
public sealed class PersonalDataDeleteClosedHandler(IDispatcher dispatcher)
    : IIntegrationEventHandler<PersonalDataDeleteClosed>
{
    public async Task HandleAsync(PersonalDataDeleteClosed integrationEvent, CancellationToken ct)
    {
        Result closed = await dispatcher.SendAsync(
            new CloseErasureCommand(integrationEvent.RequestId, integrationEvent.OccurredAt),
            ct);

        // Thrown with its error, so the retry and the error queue name the cause and not "carries no value".
        if (closed.IsFailure)
            throw new InvalidOperationException($"Closing the erasure failed: {closed.Error.Code}.");
    }
}
