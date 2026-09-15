namespace Common.Contracts.Ordering.V1;

/// <summary>
/// An order was confirmed — payment authorised, stock held (§3.2). Shipping
/// consumes it: identifiers and order facts, with no directly identifying or
/// free-text personal data (ADR-035).
/// </summary>
/// <remarks>
/// An address on the wire reaches the broker, an outbox row kept for §9.4's
/// retention window and whatever a consumer persists, beyond §11.7's erasure.
/// <c>CustomerId</c> stays; how Shipping obtains an address is Shipping's call.
/// </remarks>
public sealed record OrderConfirmed : IIntegrationEvent
{
    public required Guid MessageId { get; init; }

    public required Guid CorrelationId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid OrderId { get; init; }

    public required Guid CustomerId { get; init; }

    public required decimal TotalAmount { get; init; }

    public required string Currency { get; init; }

    public required IReadOnlyList<ConfirmedLine> Lines { get; init; }
}

/// <summary>
/// A line as <see cref="OrderConfirmed"/> carries it — its own type, for the
/// reason <see cref="PlacedLine"/> states.
/// </summary>
public sealed record ConfirmedLine(Guid ProductId, int Quantity, decimal UnitPrice);
