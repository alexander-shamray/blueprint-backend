using Common.Application;

namespace Notifications.Application.Privacy.EndErasedOrder;

/// <summary>Ends one order's rows and the contact, which the subject's erasure missed (ADR-097).</summary>
/// <remarks>
/// Scoped to the order, since the subject may have placed others after that erasure and ADR-099 keeps them. The
/// contact is the subject's, but a cache of Keycloak's, so ending it costs the next send a lookup (ADR-052). No
/// audit row, as the erasure wrote it.
/// </remarks>
public sealed class EndErasedOrderHandler(INotificationsPersonalDataStore store)
    : ICommandHandler<EndErasedOrderCommand, Result>
{
    public async Task<Result> HandleAsync(EndErasedOrderCommand command, CancellationToken ct)
    {
        await store.DeleteWaitingOrderNoticesAsync(command.OrderId, ct);
        await store.AnonymiseEndedOrderNoticesAsync(command.OrderId, ct);
        await store.DeleteWaitingOrderDeliveriesAsync(command.OrderId, ct);
        await store.AnonymiseEndedOrderDeliveriesAsync(command.OrderId, ct);
        await store.DeleteOrderRecordAsync(command.SubjectId, command.OrderId, ct);
        await store.DeleteContactAsync(command.SubjectId, ct);

        return Result.Success();
    }
}
