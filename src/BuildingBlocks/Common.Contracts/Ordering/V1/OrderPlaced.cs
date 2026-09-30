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
}

/// <summary>Each contract owns its line type, so two contracts never have to version together (§9.2).</summary>
public sealed record PlacedLine(Guid ProductId, int Quantity, decimal UnitPrice);
