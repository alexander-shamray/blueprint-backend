using Common.Domain;
using Ordering.Domain.Common;
using Ordering.Domain.Orders.Events;

namespace Ordering.Domain.Orders;

/// <summary>§5.4's aggregate: no public setters, no public parameterless constructor, no persistence.</summary>
public sealed class Order : AggregateRoot<OrderId>
{
    private readonly List<OrderLine> _lines = [];

    public CustomerId CustomerId { get; private set; }
    public OrderStatus Status { get; private set; }
    public Address ShippingAddress { get; private set; } = null!;
    public DateTimeOffset PlacedAt { get; private set; }
    public IReadOnlyList<OrderLine> Lines => _lines.AsReadOnly();

    public Money Total => _lines.Aggregate(Money.Zero(_currency), (sum, line) => sum + line.LineTotal);

    /// <summary>An immutable copy of the lines for events, since <see cref="Lines"/> is a live view.</summary>
    private IReadOnlyList<OrderLineSnapshot> SnapshotLines() =>
        [.. _lines.Select(l => new OrderLineSnapshot(l.ProductId, l.Quantity, l.UnitPrice))];

    private readonly string _currency = null!;

    // EF Core materialisation only.
    private Order() { }

    private Order(OrderId id, CustomerId customerId, Address shippingAddress, string currency, DateTimeOffset placedAt)
    {
        Id = id;
        CustomerId = customerId;
        ShippingAddress = shippingAddress;
        _currency = currency;
        PlacedAt = placedAt;
        Status = OrderStatus.Draft;
    }

    public static Order Place(
        CustomerId customerId,
        Address shippingAddress,
        IEnumerable<(ProductId Product, int Quantity, Money UnitPrice)> items,
        string currency,
        DateTimeOffset now)
    {
        // Of rather than a bare assignment: the order's currency is what every
        // line is checked against, so an unvalidated one would make AddLine's
        // comparison meaningless and Money.Zero(_currency) throw later, far
        // from the call that caused it.
        string normalised = Money.Zero(currency).Currency;

        var order = new Order(OrderId.New(), customerId, shippingAddress, normalised, now);

        foreach (var (product, quantity, unitPrice) in items)
            order.AddLine(product, quantity, unitPrice);

        if (order._lines.Count == 0)
            throw new DomainException("An order must contain at least one line.");

        order.Status = OrderStatus.AwaitingStock;
        // Lines travel with the event: the integration contract needs them so
        // Inventory can reserve stock (§9.6), and a handler must not have to
        // reload the aggregate to find out what was ordered.
        order.Raise(
            new OrderPlacedDomainEvent(order.Id, customerId, order.Total, order.SnapshotLines(), now));

        return order;
    }

    private void AddLine(ProductId product, int quantity, Money unitPrice)
    {
        if (quantity <= 0)
            throw new DomainException("Quantity must be positive.");
        if (unitPrice.Currency != _currency)
            throw new DomainException("All lines must share the order currency.");

        OrderLine? existing = _lines.SingleOrDefault(l => l.ProductId == product);
        if (existing is not null)
        {
            // The merge keeps the price already held, so a second line at a
            // different one would silently reprice the first line's quantity
            // as well — a Total no consumer could derive from the request.
            if (existing.UnitPrice != unitPrice)
                throw new DomainException("A product cannot appear twice at different prices.");

            existing.IncreaseQuantity(quantity);
        }
        else
        {
            _lines.Add(OrderLine.For(product, quantity, unitPrice));
        }
    }

    public void ConfirmStock(DateTimeOffset now)
    {
        EnsureStatus(OrderStatus.AwaitingStock);
        Status = OrderStatus.AwaitingPayment;
        Raise(new OrderStockConfirmedDomainEvent(Id, Total, now));
    }

    public void ConfirmPayment(PaymentReference reference, DateTimeOffset now)
    {
        EnsureStatus(OrderStatus.AwaitingPayment);
        Status = OrderStatus.Confirmed;
        // Total and Lines are required by the V1.OrderConfirmed contract (§9.1);
        // the mapper has only the event to work from.
        Raise(
            new OrderConfirmedDomainEvent(Id, CustomerId, reference, ShippingAddress, Total, SnapshotLines(), now));
    }

    public void MarkShipped(TrackingNumber tracking, DateTimeOffset now)
    {
        EnsureStatus(OrderStatus.Confirmed);
        Status = OrderStatus.Shipped;
        Raise(new OrderShippedDomainEvent(Id, CustomerId, tracking, now));
    }

    // Origin is recorded, never checked: no invariant here turns on who asked,
    // and §11.4 has already decided whether this caller may. It travels because
    // §9.6's saga has to tell its own echo from a cancellation somebody else
    // caused, and Reason cannot answer that — the endpoint accepts every
    // code, so a customer may cancel with the saga's own vocabulary.
    public void Cancel(CancellationReason reason, CancellationOrigin origin, DateTimeOffset now)
    {
        if (Status is OrderStatus.Shipped or OrderStatus.Delivered)
        {
            throw new DomainException(
                $"A {Status} order cannot be cancelled; raise a return instead.");
        }
        if (Status is OrderStatus.Cancelled)
            return;   // Idempotent — cancelling twice is not an error.

        Status = OrderStatus.Cancelled;
        Raise(new OrderCancelledDomainEvent(Id, CustomerId, reason, origin, now));
    }

    private void EnsureStatus(OrderStatus expected)
    {
        if (Status != expected)
        {
            throw new DomainException(
                $"Expected order to be {expected} but it is {Status}.");
        }
    }
}
