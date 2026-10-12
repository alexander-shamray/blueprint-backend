using Common.Application;
using Notifications.Application.Privacy;
using Notifications.Application.Privacy.EndErasedSubject;

namespace Notifications.Application.Intake;

/// <summary>Records an event's notice, then deletes it again when its customer was already erased (ADR-097).</summary>
/// <remarks>
/// Asked after the record's unit has committed, so an erasure that commits at any moment either finds the rows to
/// delete or is seen by the check. Only an event naming a customer can be asked about: the others wait on an order
/// record, which the erasure has already deleted.
/// </remarks>
public sealed class ErasedIntake(IDispatcher dispatcher, IErasedSubjects erased)
{
    public async Task RecordAsync(RecordNotificationCommand command, CancellationToken ct)
    {
        Result recorded = await dispatcher.SendAsync(command, ct);

        // Thrown with its error, so the retry and the error queue name the cause.
        if (recorded.IsFailure)
            throw new InvalidOperationException($"The notice was not recorded: {recorded.Error.Code}.");

        if (command.Order is OrderFact order && await erased.IsErasedAsync(order.CustomerId, ct))
            await dispatcher.SendAsync(new EndErasedSubjectCommand(order.CustomerId), ct);
    }
}
