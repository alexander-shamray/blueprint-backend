using Common.Domain;
using Ordering.Domain.Common;
using Ordering.Domain.Orders;
using Shouldly;
using Xunit;

namespace Ordering.Domain.Tests;

/// <summary>The wording of the order's currency guard and of the stock confirmation's status guard.</summary>
public class OrderBoundaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    private static Address AnAddress() =>
        Address.Of("1 Test Street", null, "Almaty", "050000", "KZ");

    private static Order AwaitingStock() =>
        Order.Place(
            new CustomerId(Guid.CreateVersion7()),
            AnAddress(),
            [(ProductId.New(), 1, Money.Of(10m, "EUR"))],
            "EUR",
            [],
            Now);

    [Fact]
    public void Place_names_the_order_currency_when_a_line_is_priced_in_another()
    {
        // Without the guard the total's own currency check refuses in other words, so the message is what pins it.
        var ex = Should.Throw<DomainException>(() =>
            Order.Place(
                new CustomerId(Guid.CreateVersion7()),
                AnAddress(),
                [(ProductId.New(), 1, Money.Of(1m, "USD"))],
                "EUR",
                [],
                Now));

        ex.Message.ShouldBe("All lines must share the order currency.");
    }

    [Fact]
    public void ConfirmStock_refuses_an_order_that_already_confirmed_it()
    {
        Order order = AwaitingStock();
        order.ConfirmStock(Now);
        int raised = order.DomainEvents.Count;

        var ex = Should.Throw<DomainException>(() => order.ConfirmStock(Now));

        ex.Message.ShouldBe("Expected order to be AwaitingStock but it is AwaitingPayment.");
        order.Status.ShouldBe(OrderStatus.AwaitingPayment);
        order.DomainEvents.Count.ShouldBe(raised);
    }
}
