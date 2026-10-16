namespace Ordering.Application.Orders.GetSagaState;

/// <summary>What the fulfilment saga says about one order that is still in flight (§9.6).</summary>
/// <remarks>
/// <c>StartedAt</c> is when the instance began; <c>StateEnteredAt</c> is when it entered <c>CurrentState</c>, and
/// null for an instance written before the column existed (§9.6).
/// </remarks>
public sealed record SagaStateView(
    Guid OrderId,
    string CurrentState,
    DateTimeOffset StartedAt,
    DateTimeOffset? StateEnteredAt,
    string? CancelReason);
