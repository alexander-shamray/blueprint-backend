namespace Web.Bff.Orders;

/// <summary>An amount with its currency, as the server computed it; the client never computes (§10.7).</summary>
public sealed record Money(decimal Amount, string Currency);

/// <summary>Each rank step's instant, keyed by name so a client never draws by position (§10.7).</summary>
public sealed record OrderTimeline(
    DateTimeOffset? Placed,
    DateTimeOffset? Confirmed,
    DateTimeOffset? Dispatched,
    DateTimeOffset? Delivered,
    DateTimeOffset? Cancelled);

/// <summary>One line as the list carries it; the name resolves on read, null before Catalog's event (§10.7).</summary>
public sealed record OrderLineSummary(Guid ProductId, string? ProductName, Money LineTotal);

/// <summary>One order as both routes carry it (§10.7, ADR-051).</summary>
/// <param name="Total">Null, and <paramref name="Lines"/> empty, while the order has no stored currency.</param>
/// <param name="AsOf">The BFF's clock at the row's last write: when it learned, not that nothing followed.</param>
public sealed record OrderSummary(
    Guid OrderId,
    string Status,
    OrderTimeline Timeline,
    bool Refunded,
    DateTimeOffset? RefundedAt,
    bool Cancellable,
    Money? Total,
    IReadOnlyList<OrderLineSummary> Lines,
    DateTimeOffset AsOf);

/// <summary>One line as the detail carries it, with what it was placed at (§10.7).</summary>
public sealed record OrderLineDetail(
    Guid ProductId,
    string? ProductName,
    Money LineTotal,
    int Quantity,
    Money UnitPrice);

/// <summary>The payment outcome there is to give; a decline has none, and the status says so (§10.7).</summary>
public sealed record PaymentFacts(DateTimeOffset? AuthorisedAt, Money? Amount, Money? RefundedAmount);

/// <summary>Shipping's two milestones, and the number a buyer takes to the carrier when stored (§10.7).</summary>
public sealed record ShipmentFacts(string? TrackingNumber, DateTimeOffset? DispatchedAt, DateTimeOffset? DeliveredAt);

/// <summary>One order as the detail route carries it: the summary's members and the three it adds (§10.7).</summary>
public sealed record OrderDetail(
    Guid OrderId,
    string Status,
    OrderTimeline Timeline,
    bool Refunded,
    DateTimeOffset? RefundedAt,
    bool Cancellable,
    Money? Total,
    IReadOnlyList<OrderLineDetail> Lines,
    DateTimeOffset AsOf,
    PaymentFacts? Payment,
    ShipmentFacts? Shipment);
