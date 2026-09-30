using Common.Domain;
using Ordering.Domain.Common;
using Ordering.Domain.Orders;
using Ordering.Domain.Orders.Events;
using Shouldly;
using Xunit;

namespace Ordering.Domain.Tests;

/// <summary>§5.4's aggregate, whose rules live in <see cref="Order"/> and nowhere else.</summary>
public class OrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    private static Address AnAddress() =>
        Address.Of("1 Test Street", null, "Almaty", "050000", "KZ");

    private static (ProductId Product, int Quantity, Money UnitPrice) AnItem(
        decimal price = 10m,
        int quantity = 1) =>
        (ProductId.New(), quantity, Money.Of(price, "EUR"));

    private static Order AnOrder(params (ProductId Product, int Quantity, Money UnitPrice)[] items) =>
        Order.Place(
            new CustomerId(Guid.CreateVersion7()),
            AnAddress(),
            items.Length == 0 ? [AnItem()] : items,
            "EUR",
            Now);

    [Fact]
    public void Place_starts_the_order_awaiting_stock_and_raises_one_event()
    {
        var customer = new CustomerId(Guid.CreateVersion7());

        Order order = Order.Place(customer, AnAddress(), [AnItem(10m, 2)], "EUR", Now);

        order.Status.ShouldBe(OrderStatus.AwaitingStock, "Draft is the constructor's state, not Place's");
        order.CustomerId.ShouldBe(customer);
        order.PlacedAt.ShouldBe(Now);
        order.Total.ShouldBe(Money.Of(20m, "EUR"));

        OrderPlacedDomainEvent placed = order.DomainEvents.ShouldHaveSingleItem()
            .ShouldBeOfType<OrderPlacedDomainEvent>();
        placed.OrderId.ShouldBe(order.Id);
        placed.CustomerId.ShouldBe(customer);
        placed.OccurredAt.ShouldBe(Now);
    }

    [Fact]
    public void Place_refuses_an_order_with_no_lines()
    {
        Should.Throw<DomainException>(() =>
            Order.Place(new CustomerId(Guid.CreateVersion7()), AnAddress(), [], "EUR", Now));
    }

    [Fact]
    public void Place_merges_two_lines_for_the_same_product()
    {
        var product = ProductId.New();
        Money price = Money.Of(5m, "EUR");

        Order order = AnOrder((product, 2, price), (product, 3, price));

        order.Lines.ShouldHaveSingleItem().Quantity.ShouldBe(5);
        order.Total.ShouldBe(Money.Of(25m, "EUR"));
    }

    [Fact]
    public void Place_refuses_the_same_product_at_two_different_prices()
    {
        // The merge keeps the price it already holds, so without this guard the €7 line would be absorbed at €5.
        var product = ProductId.New();

        Should.Throw<DomainException>(() =>
            AnOrder((product, 2, Money.Of(5m, "EUR")), (product, 3, Money.Of(7m, "EUR"))));
    }

    [Fact]
    public void Place_refuses_a_line_in_another_currency()
    {
        Should.Throw<DomainException>(() =>
            Order.Place(
                new CustomerId(Guid.CreateVersion7()),
                AnAddress(),
                [(ProductId.New(), 1, Money.Of(1m, "USD"))],
                "EUR",
                Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Place_refuses_a_non_positive_quantity(int quantity)
    {
        Should.Throw<DomainException>(() => AnOrder((ProductId.New(), quantity, Money.Of(1m, "EUR"))));
    }

    [Fact]
    public void The_lines_collection_is_a_read_only_view()
    {
        // §5.4's "callers cannot bypass AddLine": the property type carries no mutator.
        Order order = AnOrder();

        order.Lines.ShouldBeAssignableTo<IReadOnlyList<OrderLine>>();
        (order.Lines as ICollection<OrderLine>)?.IsReadOnly.ShouldBe(true);
    }

    [Fact]
    public void The_placed_event_snapshots_the_lines_rather_than_aliasing_them()
    {
        // §5.5's "snapshot, never alias": a line added later must not appear inside the earlier event.
        var product = ProductId.New();
        Money price = Money.Of(5m, "EUR");
        Order order = AnOrder((product, 1, price), (product, 1, price));

        OrderPlacedDomainEvent placed = order.DomainEvents.OfType<OrderPlacedDomainEvent>().Single();

        placed.Lines.ShouldHaveSingleItem().Quantity.ShouldBe(
            2,
            "both items merged into one line before the event was raised");
    }

    [Fact]
    public void The_happy_path_walks_the_status_machine_to_shipped()
    {
        Order order = AnOrder();

        order.ConfirmStock(Now);
        order.Status.ShouldBe(OrderStatus.AwaitingPayment);

        order.ConfirmPayment(PaymentReference.Of("pay_123"), Now);
        order.Status.ShouldBe(OrderStatus.Confirmed);

        order.MarkShipped(TrackingNumber.Of("TRK-1"), Now);
        order.Status.ShouldBe(OrderStatus.Shipped);

        order.DomainEvents.Select(e => e.GetType()).ShouldBe(
            [
                typeof(OrderPlacedDomainEvent),
                typeof(OrderStockConfirmedDomainEvent),
                typeof(OrderConfirmedDomainEvent),
                typeof(OrderShippedDomainEvent)
            ],
            "one event per transition, in the order they happened");
    }

    [Fact]
    public void A_transition_out_of_the_wrong_status_is_refused()
    {
        Order order = AnOrder();

        // AwaitingStock, so payment has nothing to confirm yet.
        Should.Throw<DomainException>(() => order.ConfirmPayment(PaymentReference.Of("pay_1"), Now));
        Should.Throw<DomainException>(() => order.MarkShipped(TrackingNumber.Of("TRK-1"), Now));
    }

    [Fact]
    public void Cancel_moves_to_cancelled_and_raises_the_event_with_its_reason()
    {
        Order order = AnOrder();

        order.Cancel(CancellationReason.CustomerRequest, CancellationOrigin.User, Now);

        order.Status.ShouldBe(OrderStatus.Cancelled);
        OrderCancelledDomainEvent cancelled = order.DomainEvents
            .OfType<OrderCancelledDomainEvent>()
            .ShouldHaveSingleItem();
        cancelled.Reason.ShouldBe(CancellationReason.CustomerRequest);
        cancelled.Origin.ShouldBe(CancellationOrigin.User);
        cancelled.CustomerId.ShouldBe(order.CustomerId);
    }

    [Theory]
    [InlineData(CancellationReason.CustomerRequest, CancellationOrigin.Workflow)]
    [InlineData(CancellationReason.PaymentDeclined, CancellationOrigin.User)]
    public void The_origin_is_carried_independently_of_the_reason(
        CancellationReason reason,
        CancellationOrigin origin)
    {
        // Pairings a derived origin would get wrong, both reachable because §11.4's endpoint parses every reason.
        Order order = AnOrder();

        order.Cancel(reason, origin, Now);

        OrderCancelledDomainEvent cancelled = order.DomainEvents
            .OfType<OrderCancelledDomainEvent>()
            .ShouldHaveSingleItem();
        cancelled.Reason.ShouldBe(reason);
        cancelled.Origin.ShouldBe(origin);
    }

    [Fact]
    public void Cancel_is_idempotent_and_raises_nothing_the_second_time()
    {
        // §5.4: events arrive at-least-once, so a repeated cancellation is not
        // an error — and must not stage a second outbox row either.
        Order order = AnOrder();

        order.Cancel(CancellationReason.OutOfStock, CancellationOrigin.Workflow, Now);
        order.Cancel(CancellationReason.CustomerRequest, CancellationOrigin.User, Now.AddMinutes(1));

        order.Status.ShouldBe(OrderStatus.Cancelled);
        order.DomainEvents.OfType<OrderCancelledDomainEvent>().ShouldHaveSingleItem()
            .Reason.ShouldBe(CancellationReason.OutOfStock, "the first cancellation is the one that happened");
    }

    [Fact]
    public void A_shipped_order_cannot_be_cancelled()
    {
        Order order = AnOrder();
        order.ConfirmStock(Now);
        order.ConfirmPayment(PaymentReference.Of("pay_1"), Now);
        order.MarkShipped(TrackingNumber.Of("TRK-1"), Now);

        Should.Throw<DomainException>(
            () => order.Cancel(CancellationReason.CustomerRequest, CancellationOrigin.User, Now));
    }

    [Fact]
    public void Clearing_the_events_leaves_the_state_alone()
    {
        Order order = AnOrder();

        order.ClearDomainEvents();

        order.DomainEvents.ShouldBeEmpty();
        order.Status.ShouldBe(OrderStatus.AwaitingStock);
    }
}
