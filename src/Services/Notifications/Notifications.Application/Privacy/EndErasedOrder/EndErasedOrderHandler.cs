using Common.Application;

namespace Notifications.Application.Privacy.EndErasedOrder;

/// <summary>Ends one order's rows and the contact, which the subject's erasure missed (ADR-097).</summary>
/// <remarks>
/// Scoped to the order, since ADR-099 keeps the others the subject placed after that erasure. The contact is a cache
/// of Keycloak's, so ending it costs the next send a lookup (ADR-052). No audit row, as the erasure wrote it.
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
