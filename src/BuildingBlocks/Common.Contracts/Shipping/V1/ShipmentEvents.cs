namespace Common.Contracts.Shipping.V1;

/// <summary>The saga marks the order shipped and finalises on it (§9.6).</summary>
public sealed record ShipmentDispatched : IIntegrationEvent
{
    public required Guid MessageId { get; init; }

    public required Guid CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid OrderId { get; init; }

    public required string TrackingNumber { get; init; }
}

/// <summary>Notifications' alone: the saga finalised on despatch (§9.6).</summary>
public sealed record ShipmentDelivered : IIntegrationEvent
{
    public required Guid MessageId { get; init; }

    public required Guid CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid OrderId { get; init; }

    public required string TrackingNumber { get; init; }
}
