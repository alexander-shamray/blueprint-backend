namespace Common.Contracts.Ordering.V1;

/// <summary>The fact the fulfilment saga starts on (§9.6).</summary>
public sealed record OrderPlaced : IIntegrationEvent
{
    public required Guid MessageId { get; init; }

    public required Guid CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid OrderId { get; init; }

    public required Guid CustomerId { get; init; }

    public required decimal TotalAmount { get; init; }

    public required string Currency { get; init; }

    public required IReadOnlyList<PlacedLine> Lines { get; init; }

    /// <summary>The requests the customer was already erased under when the order was placed (ADR-099).</summary>
    /// <remarks>Optional, so absent means an older publisher and an empty list alike (§9.2).</remarks>
    public IReadOnlyList<Guid>? ErasedUnder { get; init; }
}

/// <summary>Each contract owns its line type, so two contracts never have to version together (§9.2).</summary>
public sealed record PlacedLine(Guid ProductId, int Quantity, decimal UnitPrice);
