using Common.Application;
using Common.Contracts.Privacy.V1;

namespace Payments.Application.Privacy.ErasePersonalData;

/// <summary>Erases what Payments holds of the subject, with its audit row in one unit (ADR-092).</summary>
public sealed class PersonalDataDeleteRequestedHandler(IDispatcher dispatcher)
    : IIntegrationEventHandler<PersonalDataDeleteRequested>
{
    public async Task HandleAsync(PersonalDataDeleteRequested integrationEvent, CancellationToken ct) =>
        await dispatcher.SendAsync(
            new ErasePersonalDataCommand(integrationEvent.RequestId, integrationEvent.SubjectId),
            ct);
}
