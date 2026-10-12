namespace Common.Contracts.Ordering.V1;

/// <summary>Inventory releases, Payments voids and the saga stops on it (§3.2, §11.4).</summary>
public sealed record OrderCancelled : IIntegrationEvent
{
    public required Guid MessageId { get; init; }

    public required Guid CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid OrderId { get; init; }

    public required Guid CustomerId { get; init; }

    public required string Reason { get; init; }

    /// <summary>A <see cref="CancelOrigins"/> code; optional, so absent means an older publisher (§9.2).</summary>
    public string? Origin { get; init; }

    /// <summary>The requests the customer was already erased under when the order was placed (ADR-099).</summary>
    /// <remarks>Optional, so absent means an older publisher and an empty list alike (§9.2).</remarks>
    public IReadOnlyList<Guid>? ErasedUnder { get; init; }
}
