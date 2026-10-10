using Common.Application;
using Common.Contracts.Privacy.V1;
using Shipping.Application.Privacy.EndWaitingShipment;

namespace Shipping.Application.Privacy.ErasePersonalData;

/// <summary>Ends what waits on the subject's addresses, erases them, then tells Privacy (ADR-094).</summary>
/// <remarks>
/// The shipments end first, while the rows that name their orders still exist, so a redelivery after a crash between
/// the steps finds the same orders and repeats harmlessly. Reported here and not in a command, because the execution
/// strategy re-runs a command whole and a send inside it would repeat (ADR-094); the consumer's in-memory outbox
/// holds it until this handler returns.
/// </remarks>
public sealed class PersonalDataDeleteRequestedHandler(
    IDispatcher dispatcher,
    IShippingPersonalDataStore store,
    IErasureReporter reporter)
    : IIntegrationEventHandler<PersonalDataDeleteRequested>
{
    public async Task HandleAsync(PersonalDataDeleteRequested integrationEvent, CancellationToken ct)
    {
        foreach (Guid order in await store.AddressedOrdersAsync(integrationEvent.SubjectId, ct))
        {
            Result ended = await dispatcher.SendAsync(new EndWaitingShipmentCommand(order), ct);

            // Thrown with its error, so the retry and the error queue name the cause and not "carries no value".
            if (ended.IsFailure)
                throw new InvalidOperationException($"Ending the shipment failed: {ended.Error.Code}.");
        }

        Result<int> erased = await dispatcher.SendAsync(
            new ErasePersonalDataCommand(integrationEvent.RequestId, integrationEvent.SubjectId),
            ct);

        if (erased.IsFailure)
            throw new InvalidOperationException($"The erasure failed: {erased.Error.Code}.");

        // Even a count of zero is reported: silence cannot be told from success (§11.7).
        await reporter.ReportAsync(integrationEvent.RequestId, erased.Value, ct);
    }
}
