namespace Ordering.Application.Orders.GetSagaState;

/// <summary>What the fulfilment saga says about one order that is still in flight (§9.6).</summary>
/// <remarks>
/// <c>StartedAt</c> is when the instance began: the saga stores no time of entering a state, and adding one is
/// not this read's decision.
/// </remarks>
public sealed record SagaStateView(Guid OrderId, string CurrentState, DateTimeOffset StartedAt, string? CancelReason);
