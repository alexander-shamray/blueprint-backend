using Common.Application;

namespace Payments.Application.Orders.EndErasedOrder;

/// <summary>Takes the payer off one order recorded for a subject Payments had already erased (ADR-097).</summary>
public sealed record EndErasedOrderCommand(Guid OrderId, Guid CustomerId) : ICommand<Result>;
