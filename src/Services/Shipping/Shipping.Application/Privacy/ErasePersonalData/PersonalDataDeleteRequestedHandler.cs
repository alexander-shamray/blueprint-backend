using Common.Application;
using Common.Contracts.Privacy.V1;

namespace Shipping.Application.Privacy.ErasePersonalData;

/// <summary>Erases the delivery addresses Shipping holds for the subject, then tells Privacy once the unit commits (ADR-094).</summary>
/// <remarks>
/// Reported here and not in the command, because the execution strategy re-runs a command whole and a send inside
/// it would repeat (ADR-094); the consumer's in-memory outbox holds it until this handler returns.
/// </remarks>
public sealed class PersonalDataDeleteRequestedHandler(IDispatcher dispatcher, IErasureReporter reporter)
    : IIntegrationEventHandler<PersonalDataDeleteRequested>
{
    public async Task HandleAsync(PersonalDataDeleteRequested integrationEvent, CancellationToken ct)
    {
        Result<int> erased = await dispatcher.SendAsync(
            new ErasePersonalDataCommand(integrationEvent.RequestId, integrationEvent.SubjectId),
            ct);

        // Thrown with its error, so the retry and the error queue name the cause and not "carries no value".
        if (erased.IsFailure)
            throw new InvalidOperationException($"The erasure failed: {erased.Error.Code}.");

        // Even a count of zero is reported: silence cannot be told from success (§11.7).
        await reporter.ReportAsync(integrationEvent.RequestId, erased.Value, ct);
    }
}
