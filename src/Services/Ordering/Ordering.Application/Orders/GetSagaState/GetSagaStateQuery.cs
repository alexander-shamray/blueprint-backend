using Common.Application;

namespace Ordering.Application.Orders.GetSagaState;

public sealed record GetSagaStateQuery(Guid OrderId) : IQuery<SagaStateView?>;
