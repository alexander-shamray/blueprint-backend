using Common.Domain;
using Payments.Domain.Intents;
using Payments.Domain.Intents.Events;
using Payments.Domain.Orders;
using Shouldly;
using Xunit;

namespace Payments.Domain.Tests;

public class PaymentIntentTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Authorise_records_the_reference_and_raises_the_authorisation()
    {
        OrderId order = OrderId.New();

        PaymentIntent intent = PaymentIntent.Authorise(order, 42.10m, "EUR", "psp_1", Now);

        intent.Status.ShouldBe(PaymentIntentStatus.Authorised);
        intent.Reference.ShouldBe("psp_1");
        intent.DomainEvents.ShouldHaveSingleItem()
            .ShouldBe(new PaymentAuthorisedDomainEvent(order, "psp_1", 42.10m, "EUR", Now));
    }

    [Fact]
    public void Decline_records_the_reason_and_raises_the_decline()
    {
        OrderId order = OrderId.New();

        PaymentIntent intent = PaymentIntent.Decline(order, 42.10m, "EUR", "card_declined", Now);

        intent.Status.ShouldBe(PaymentIntentStatus.Declined);
        intent.Reference.ShouldBeNull();
        intent.DomainEvents.ShouldHaveSingleItem()
            .ShouldBe(new PaymentDeclinedDomainEvent(order, "card_declined", Now));
    }

    [Fact]
    public void An_authorisation_needs_a_reference_and_a_decline_a_reason()
    {
        Should.Throw<DomainException>(() => PaymentIntent.Authorise(OrderId.New(), 1m, "EUR", " ", Now));
        Should.Throw<DomainException>(() => PaymentIntent.Decline(OrderId.New(), 1m, "EUR", "", Now));
    }
}
