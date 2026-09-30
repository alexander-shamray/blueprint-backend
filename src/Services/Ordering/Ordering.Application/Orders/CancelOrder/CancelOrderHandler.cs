using Common.Application;
using Common.Domain;
using Ordering.Domain.Orders;

namespace Ordering.Application.Orders.CancelOrder;

/// <summary>§11.4's resource-level check, here where the order is loaded.</summary>
/// <remarks>Public, because §6.2's scan registers public classes only.</remarks>
public sealed class CancelOrderHandler(IOrderRepository orders, ICurrentUser currentUser, TimeProvider clock)
    : ICommandHandler<CancelOrderCommand, Result>
{
    public async Task<Result> HandleAsync(CancelOrderCommand command, CancellationToken ct)
    {
        Order? order = await orders.GetAsync(new OrderId(command.OrderId), ct);
        if (order is null)
            return Result.Failure(OrderErrors.NotFound);

        // A 404 rather than a 403, which would confirm the order exists. "orders:admin" is a claim checked against
        // a loaded aggregate, not one of OrderingPermissions' policies (§11.4).
        if (!command.IsSystemInitiated &&
            (!currentUser.IsAuthenticated ||
                (order.CustomerId.Value != currentUser.Id &&
                    !currentUser.HasPermission("orders:admin"))))
        {
            return Result.Failure(OrderErrors.NotFound);
        }

        // Anything not provably the workflow is User, so the saga faults rather than discards (§9.6, §11.4).
        CancellationOrigin origin = command.InitiatedBy switch
        {
            CommandOrigin.System => CancellationOrigin.Workflow,
            _ => CancellationOrigin.User
        };

        // The aggregate owns the transition (§5.4); its refusal past despatch is a 422, not a 500.
        try
        {
            order.Cancel(command.Reason, origin, clock.GetUtcNow());
        }
        catch (DomainException)
        {
            return Result.Failure(OrderErrors.AlreadyShipped);
        }

        // No metric here: a count inside the transaction is repeated by a replay (§6.4, §13.3).

        // No SaveChangesAsync: TransactionBehavior owns the commit (§6.3).
        return Result.Success();
    }
}
