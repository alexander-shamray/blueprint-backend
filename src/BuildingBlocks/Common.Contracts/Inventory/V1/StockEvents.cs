namespace Common.Contracts.Inventory.V1;

/// <summary>The saga's cue to authorise payment (§9.6).</summary>
public sealed record StockReserved : IIntegrationEvent
{
    public required Guid MessageId { get; init; }

    public required Guid CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid OrderId { get; init; }
}

/// <summary>Carries the failed ids, because the saga finalises on it before anyone can ask (§9.6).</summary>
public sealed record StockReservationFailed : IIntegrationEvent
{
    public required Guid MessageId { get; init; }

    public required Guid CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid OrderId { get; init; }

    public required IReadOnlyList<Guid> UnavailableProductIds { get; init; }
}

/// <summary>A postcondition, so it may arrive unasked and carries no quantity (ADR-024).</summary>
public sealed record StockReleased : IIntegrationEvent
{
    public required Guid MessageId { get; init; }

    public required Guid CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid OrderId { get; init; }
}

/// <summary>A level, not a delta, so a redelivery never double-counts (§6.6).</summary>
public sealed record StockLevelChanged : IIntegrationEvent
{
    public required Guid MessageId { get; init; }

    public required Guid CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid ProductId { get; init; }

    public required int QuantityAvailable { get; init; }
}
