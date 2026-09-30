namespace Common.Contracts.Payments.V1;

/// <summary>The saga confirms the order on it, passing <see cref="Reference"/> on (§9.6).</summary>
public sealed record PaymentAuthorised : IIntegrationEvent
{
    public required Guid MessageId { get; init; }

    public required Guid CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid OrderId { get; init; }

    public required string Reference { get; init; }

    public required decimal Amount { get; init; }

    public required string Currency { get; init; }
}

/// <summary><see cref="Reason"/> is an open code for a human, never branched on or a dimension (ADR-049).</summary>
public sealed record PaymentDeclined : IIntegrationEvent
{
    public required Guid MessageId { get; init; }

    public required Guid CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid OrderId { get; init; }

    public required string Reason { get; init; }
}

/// <summary>Published because Notifications consumes it, and §3.2 closes in both directions.</summary>
public sealed record PaymentRefunded : IIntegrationEvent
{
    public required Guid MessageId { get; init; }

    public required Guid CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid OrderId { get; init; }

    public required string Reference { get; init; }

    public required decimal Amount { get; init; }

    public required string Currency { get; init; }
}
