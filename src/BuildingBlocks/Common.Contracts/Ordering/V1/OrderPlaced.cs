namespace Common.Contracts.Ordering.V1;

/// <summary>
/// An order was placed (§3.2). The fact the fulfilment saga starts on (§9.6)
/// and the one <c>ReserveStock</c> draws its lines from.
/// </summary>
/// <remarks>
/// The envelope's three members are written out rather than inherited from a
/// base record: a shared base is a shared versioning fate (§9.2), and three
/// properties is a cheaper price than that.
/// </remarks>
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

/// <summary>
/// A line as <see cref="OrderPlaced"/> carries it.
/// </summary>
/// <remarks>
/// Each contract owns its line type. This and <see cref="ConfirmedLine"/>
/// have identical shapes today and sharing one record would be the obvious
/// economy. It is the wrong one: a field added to <c>OrderConfirmed</c>'s lines
/// would silently change this payload, and the two contracts would have to
/// version together — the coupling §9.2 exists to prevent.
/// </remarks>
public sealed record PlacedLine(Guid ProductId, int Quantity, decimal UnitPrice);
