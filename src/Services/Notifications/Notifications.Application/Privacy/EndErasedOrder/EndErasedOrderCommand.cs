using Common.Application;

namespace Notifications.Application.Privacy.EndErasedOrder;

/// <summary>Deletes what an event wrote for one order of a subject Notifications had already erased (ADR-097).</summary>
public sealed record EndErasedOrderCommand(Guid SubjectId, Guid OrderId) : ICommand<Result>;
