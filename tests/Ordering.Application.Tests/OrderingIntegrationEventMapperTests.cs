using Common.Application;
using Common.Contracts;
using Common.Contracts.Ordering.V1;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Application;
using Ordering.Domain.Common;
using Ordering.Domain.Orders;
using Ordering.Domain.Orders.Events;
using Shouldly;
using System.Text.Json;
using Xunit;

namespace Ordering.Application.Tests;

/// <summary>§9.3's allow-list for Ordering, resolved through <c>AddOrderingApplication</c>, not constructed.</summary>
public class OrderingIntegrationEventMapperTests
{
    private static readonly DateTimeOffset Raised = new(2026, 8, 19, 9, 0, 0, TimeSpan.Zero);

    private static readonly OrderId Order = OrderId.New();

    private static readonly CustomerId Customer = new(Guid.CreateVersion7());

    private static readonly ProductId Product = ProductId.New();

    private static IIntegrationEventMapper Mapper()
    {
        ServiceCollection services = new();
        services.AddOrderingApplication();

        return services
            .BuildServiceProvider()
            .CreateScope()
            .ServiceProvider
            .GetRequiredService<IIntegrationEventMapper>();
    }

    private static IReadOnlyList<OrderLineSnapshot> Lines() =>
        [new OrderLineSnapshot(Product, 2, Money.Of(64.99m, "EUR"))];

    [Fact]
    public void A_placed_order_becomes_its_contract()
    {
        IReadOnlyList<object> mapped = Mapper().Map(
            [new OrderPlacedDomainEvent(Order, Customer, Money.Of(129.98m, "EUR"), Lines(), Raised)]);

        OrderPlaced placed = mapped.ShouldHaveSingleItem().ShouldBeOfType<OrderPlaced>();

        placed.OrderId.ShouldBe(Order.Value);
        placed.CustomerId.ShouldBe(Customer.Value);
        placed.TotalAmount.ShouldBe(129.98m);
        placed.Currency.ShouldBe("EUR");
        placed.OccurredAt.ShouldBe(Raised);

        // The order, not a request id, because §9.6's saga correlates its instance on the same value.
        placed.CorrelationId.ShouldBe(Order.Value);
        placed.MessageId.ShouldNotBe(Guid.Empty);

        // The lines are what ReserveStock is drawn from (§9.6).
        PlacedLine line = placed.Lines.ShouldHaveSingleItem();
        line.ProductId.ShouldBe(Product.Value);
        line.Quantity.ShouldBe(2);
        line.UnitPrice.ShouldBe(64.99m);
    }

    [Fact]
    public void A_confirmed_order_carries_identifiers_and_no_address()
    {
        // Fully populated, since an empty address could not tell a mapper that drops it from one that never had it.
        Address address = Address.Of("12 Rue de la Paix", "Appartement 4", "Paris", "75002", "FR");

        IReadOnlyList<object> mapped = Mapper().Map(
            [
                new OrderConfirmedDomainEvent(
                    Order,
                    Customer,
                    PaymentReference.Of("psp-ref-1"),
                    address,
                    Money.Of(129.98m, "EUR"),
                    Lines(),
                    Raised)
            ]);

        OrderConfirmed confirmed = mapped.ShouldHaveSingleItem().ShouldBeOfType<OrderConfirmed>();

        // The serialised payload, not the type, because the wire is what ADR-035 is about.
        string payload = JsonSerializer.Serialize(confirmed);

        // Bare substrings catch an address concatenated into one value; only the country is quoted, since a
        // bare FR matches inside the currency's EUR.
        foreach (string component in (string[])
            ["12 Rue de la Paix", "Appartement 4", "Paris", "75002"])
        {
            payload.ShouldNotContain(
                component,
                Case.Sensitive,
                $"§11.7: no part of the delivery address may reach the wire, and '{component}' did");
        }

        payload.ShouldNotContain(
            "\"FR\"",
            Case.Sensitive,
            "§11.7: no part of the delivery address may reach the wire, and the country did");

        ConfirmedLine line = confirmed.Lines.ShouldHaveSingleItem();
        line.ProductId.ShouldBe(Product.Value);
        line.UnitPrice.ShouldBe(64.99m);

        confirmed.CustomerId.ShouldBe(Customer.Value);
    }

    [Fact]
    public void A_cancellation_carries_the_wire_code_and_not_the_enum_name()
    {
        // CancellationReason's member names are not the contract, so a rename cannot break a consumer.
        IReadOnlyList<object> mapped = Mapper().Map(
            [new OrderCancelledDomainEvent(
                Order,
                Customer,
                CancellationReason.PaymentTimeout,
                CancellationOrigin.Workflow,
                Raised)]);

        OrderCancelled cancelled = mapped.ShouldHaveSingleItem().ShouldBeOfType<OrderCancelled>();

        cancelled.Reason.ShouldBe(
            CancelReasons.PaymentTimeout,
            "the saga sends payment_timeout and the published fact has to say the same thing — §13.3 " +
            "tags orders.cancelled with it, and a decline and a silent PSP are a different incident");
        cancelled.Reason.ShouldNotBe(nameof(CancellationReason.PaymentTimeout));
    }

    [Theory]
    [InlineData(CancellationOrigin.User, CancelOrigins.User)]
    [InlineData(CancellationOrigin.Workflow, CancelOrigins.Workflow)]
    public void A_cancellation_carries_its_origin_onto_the_wire(
        CancellationOrigin origin,
        string expected)
    {
        // The saga reads this field alone to tell its own echo, and absent is a legal shape on the contract.
        IReadOnlyList<object> mapped = Mapper().Map(
            [
                new OrderCancelledDomainEvent(
                    Order,
                    Customer,
                    CancellationReason.CustomerRequest,
                    origin,
                    Raised)
            ]);

        OrderCancelled cancelled = mapped.ShouldHaveSingleItem().ShouldBeOfType<OrderCancelled>();

        cancelled.Origin.ShouldBe(expected);
        cancelled.Origin.ShouldNotBe(nameof(CancellationOrigin.Workflow));
    }

    [Fact]
    public void The_two_local_events_reach_no_contract()
    {
        // §3.2's Publishes column; OrderShipped is Shipping's fact, and republishing it would give it two owners.
        IReadOnlyList<object> mapped = Mapper().Map(
            [
                new OrderStockConfirmedDomainEvent(Order, Money.Of(129.98m, "EUR"), Raised),
                new OrderShippedDomainEvent(Order, Customer, TrackingNumber.Of("TRACK-1"), Raised)
            ]);

        mapped.ShouldBeEmpty("an unregistered domain event is local-only, and that is not an error");
    }

    [Fact]
    public void Every_mapped_event_mints_its_own_message_id()
    {
        // The id is the outbox key, the header and the inbox key at once (§9.1), so a shared one drops the second.
        IReadOnlyList<object> mapped = Mapper().Map(
            [
                new OrderPlacedDomainEvent(Order, Customer, Money.Of(129.98m, "EUR"), Lines(), Raised),
                new OrderCancelledDomainEvent(
                    Order,
                    Customer,
                    CancellationReason.OutOfStock,
                    CancellationOrigin.Workflow,
                    Raised)
            ]);

        mapped.Count.ShouldBe(2);

        Guid[] ids = [.. mapped.Cast<IIntegrationEvent>().Select(e => e.MessageId)];

        ids.ShouldBeUnique();
        ids.ShouldAllBe(id => id != Guid.Empty);
    }
}
