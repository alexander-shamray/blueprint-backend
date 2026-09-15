namespace Common.Contracts.Inventory.V1;

/// <summary>
/// Stock was held for an order (§3.2). The saga's cue to authorise payment
/// (§9.6).
/// </summary>
public sealed record StockReserved : IIntegrationEvent
{
    public required Guid MessageId { get; init; }

    public required Guid CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid OrderId { get; init; }
}

/// <summary>
/// Stock could not be held for an order (§3.2). The saga cancels on it (§9.6).
/// </summary>
/// <remarks>
/// <see cref="UnavailableProductIds"/> is carried because a saga awaiting stock
/// finalises on this event, so its instance is gone before anyone asks which
/// lines failed; a late one in <c>Compensating</c> is ignored.
/// Ids rather than a message: a consumer wanting names has a product read
/// model, and a sentence on a contract is one every consumer must parse.
/// </remarks>
public sealed record StockReservationFailed : IIntegrationEvent
{
    public required Guid MessageId { get; init; }

    public required Guid CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid OrderId { get; init; }

    public required IReadOnlyList<Guid> UnavailableProductIds { get; init; }
}

/// <summary>
/// No stock is held for this order (§3.2) — a fact rather than an undo (§9.6).
/// </summary>
/// <remarks>
/// A postcondition, not a state change (ADR-024): a <c>ReleaseStock</c>, an
/// <c>OrderCancelled</c> Inventory consumes directly, and a
/// <see cref="ReserveStock"/> refused against the tombstone all publish it, so
/// §9.6's saga may receive one in a state it sent no release from. There may
/// have been nothing to count, which is why it carries no quantity.
/// </remarks>
public sealed record StockReleased : IIntegrationEvent
{
    public required Guid MessageId { get; init; }

    public required Guid CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid OrderId { get; init; }
}

/// <summary>
/// The available quantity for a product moved (§3.2); Catalog consumes it.
/// </summary>
/// <remarks>
/// <see cref="QuantityAvailable"/> is a level, not a delta: a redelivered level
/// does not double-count. An older level arriving late is §6.6's other problem,
/// and a projection of it needs its own watermark on <c>OccurredAt</c>.
/// </remarks>
public sealed record StockLevelChanged : IIntegrationEvent
{
    public required Guid MessageId { get; init; }

    public required Guid CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid ProductId { get; init; }

    public required int QuantityAvailable { get; init; }
}
