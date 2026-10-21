using Common.Application;
using Notifications.Application.Privacy;
using Notifications.Application.Privacy.EndErasedOrder;

namespace Notifications.Application.Intake;

/// <summary>Records an event's notice, then deletes its order if its customer was already erased (ADR-097).</summary>
/// <remarks>
/// Asked after the record's unit commits, so an erasure at any moment finds the rows or is seen (ADR-097). The
/// event's own instant bounds it, as the data left its owner then (ADR-139).
/// </remarks>
public sealed class ErasedIntake(IDispatcher dispatcher, IErasedSubjects erased)
{
    public async Task RecordAsync(RecordNotificationCommand command, CancellationToken ct)
    {
        Result recorded = await dispatcher.SendAsync(command, ct);

        // Thrown with its error, so the retry and the error queue name the cause.
        if (recorded.IsFailure)
            throw new InvalidOperationException($"The notice was not recorded: {recorded.Error.Code}.");

        if (command.Order is OrderFact order &&
            await erased.IsErasedAsync(
                order.CustomerId,
                order.ErasedUnder ?? [],
                command.Parameters.OccurredAt,
                ct))
        {
            await dispatcher.SendAsync(new EndErasedOrderCommand(order.CustomerId, command.Parameters.OrderId), ct);
        }
    }
}
