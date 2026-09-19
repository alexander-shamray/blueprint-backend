using Common.Application;
using Common.Contracts.Payments.V1;
using Common.Domain;
using Microsoft.Extensions.DependencyInjection;
using Payments.Domain.Intents.Events;
using Payments.Domain.Orders;
using Payments.Domain.Refunds.Events;
using Shouldly;
using Xunit;

namespace Payments.Application.Tests;

public class PaymentsIntegrationEventMapperTests
{
    private static readonly DateTimeOffset Raised = new(2026, 9, 18, 9, 0, 0, TimeSpan.Zero);

    private static IIntegrationEventMapper Mapper()
    {
        ServiceCollection services = new();
        services.AddPaymentsApplication();
        return services
            .BuildServiceProvider()
            .CreateScope()
            .ServiceProvider
            .GetRequiredService<IIntegrationEventMapper>();
    }

    [Fact]
    public void An_authorisation_becomes_PaymentAuthorised_correlated_on_the_order()
    {
        OrderId order = OrderId.New();

        PaymentAuthorised contract = Mapper()
            .Map([new PaymentAuthorisedDomainEvent(order, "psp_1", 42.10m, "EUR", Raised)])
            .ShouldHaveSingleItem().ShouldBeOfType<PaymentAuthorised>();

        contract.OrderId.ShouldBe(order.Value);
        contract.CorrelationId.ShouldBe(order.Value, "§9.6's saga correlates on the order");
        contract.Reference.ShouldBe("psp_1");
        contract.Amount.ShouldBe(42.10m);
        contract.Currency.ShouldBe("EUR");
        contract.OccurredAt.ShouldBe(Raised);
        contract.MessageId.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public void A_decline_becomes_PaymentDeclined_with_its_reason()
    {
        OrderId order = OrderId.New();

        PaymentDeclined contract = Mapper()
            .Map([new PaymentDeclinedDomainEvent(order, "order_cancelled", Raised)])
            .ShouldHaveSingleItem().ShouldBeOfType<PaymentDeclined>();

        contract.OrderId.ShouldBe(order.Value);
        contract.CorrelationId.ShouldBe(order.Value);
        contract.Reason.ShouldBe("order_cancelled");
    }

    [Fact]
    public void A_refund_becomes_PaymentRefunded_correlated_on_the_order()
    {
        OrderId order = OrderId.New();

        PaymentRefunded contract = Mapper()
            .Map([new PaymentRefundedDomainEvent(order, "psp_1", 42.10m, "EUR", Raised)])
            .ShouldHaveSingleItem().ShouldBeOfType<PaymentRefunded>();

        contract.OrderId.ShouldBe(order.Value);
        contract.CorrelationId.ShouldBe(order.Value);
        contract.Reference.ShouldBe("psp_1");
        contract.Amount.ShouldBe(42.10m);
        contract.Currency.ShouldBe("EUR");
        contract.OccurredAt.ShouldBe(Raised);
        contract.MessageId.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public void The_three_registered_events_are_the_Publishes_column_and_exactly_it()
    {
        // §3.2's Publishes column for Payments is three facts, and the
        // allow-list is what makes that structural rather than reviewed: one
        // commit raising all three stages three contracts and no fourth.
        OrderId order = OrderId.New();

        IReadOnlyList<object> mapped = Mapper().Map(
            [
                new PaymentAuthorisedDomainEvent(order, "psp_1", 42.10m, "EUR", Raised),
                new PaymentDeclinedDomainEvent(order, "card_declined", Raised),
                new PaymentRefundedDomainEvent(order, "psp_1", 42.10m, "EUR", Raised)
            ]);

        mapped.Select(c => c.GetType()).ShouldBe(
            [typeof(PaymentAuthorised), typeof(PaymentDeclined), typeof(PaymentRefunded)],
            ignoreOrder: true);
    }

    [Fact]
    public void An_unregistered_domain_event_reaches_no_contract()
    {
        // §9.3's allow-list read from the other side: absence from the registry
        // is what keeps a domain event off the bus, and it is not an error.
        Mapper().Map([new UnpublishedDomainEvent(Raised)])
            .ShouldBeEmpty("an unregistered domain event is local-only, and that is not an error");
    }

    private sealed record UnpublishedDomainEvent(DateTimeOffset OccurredAt) : IDomainEvent;
}
