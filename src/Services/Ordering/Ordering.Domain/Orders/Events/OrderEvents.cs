using Common.Domain;
using Ordering.Domain.Common;

namespace Ordering.Domain.Orders.Events;

/// <summary>Immutable copy of a line as it stood when the event was raised.</summary>
public sealed record OrderLineSnapshot(ProductId ProductId, int Quantity, Money UnitPrice);

public sealed record OrderPlacedDomainEvent(
    OrderId OrderId,
    CustomerId CustomerId,
    Money Total,
    IReadOnlyList<OrderLineSnapshot> Lines,
    IReadOnlyList<Guid> ErasedUnder,
    DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record OrderConfirmedDomainEvent(
    OrderId OrderId,
    CustomerId CustomerId,
    PaymentReference Reference,
    Address ShippingAddress,
    Money Total,
    IReadOnlyList<OrderLineSnapshot> Lines,
    IReadOnlyList<Guid> ErasedUnder,
    DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record OrderStockConfirmedDomainEvent(
    OrderId OrderId,
    Money Total,
    DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record OrderShippedDomainEvent(
    OrderId OrderId,
    CustomerId CustomerId,
    TrackingNumber Tracking,
    DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record OrderCancelledDomainEvent(
    OrderId OrderId,
    CustomerId CustomerId,
    CancellationReason Reason,
    CancellationOrigin Origin,
    IReadOnlyList<Guid> ErasedUnder,
    DateTimeOffset OccurredAt) : IDomainEvent;
