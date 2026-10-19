using Common.Application;

namespace Notifications.Application.Privacy.EndErasedOrder;

/// <summary>Ends one order's notices and record, which the subject's erasure missed (ADR-097).</summary>
/// <remarks>
/// Scoped to the order, since the subject may have placed others after that erasure and ADR-099 keeps them. The
/// notices go before the record they are found through; no audit row, as the erasure wrote it.
/// </remarks>
public sealed class EndErasedOrderHandler(INotificationsPersonalDataStore store)
    : ICommandHandler<EndErasedOrderCommand, Result>
{
    public async Task<Result> HandleAsync(EndErasedOrderCommand command, CancellationToken ct)
    {
        await store.DeleteWaitingOrderNoticesAsync(command.OrderId, ct);
        await store.AnonymiseEndedOrderNoticesAsync(command.OrderId, ct);
        await store.DeleteOrderRecordAsync(command.SubjectId, command.OrderId, ct);

        return Result.Success();
    }
}
