using Common.Application;
using Common.Contracts.Payments.V1;
using Microsoft.Extensions.DependencyInjection;
using Payments.Domain.Intents.Events;
using Payments.Domain.Orders;
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
        return services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<IIntegrationEventMapper>();
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
}
