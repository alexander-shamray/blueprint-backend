using Common.Domain;
using Payments.Domain.Intents;
using Payments.Domain.Orders;
using Payments.Domain.Refunds;
using Payments.Domain.Refunds.Events;
using Shouldly;
using Xunit;

namespace Payments.Domain.Tests;

public class RefundTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_void_of_an_authorised_intent_carries_its_reference_and_money_and_raises_the_refund()
    {
        OrderId order = OrderId.New();
        PaymentIntent intent = PaymentIntent.Authorise(order, 42.10m, "EUR", "psp_1", Now.AddMinutes(-5));

        Refund refund = Refund.Voided(intent, Now);

        refund.Id.ShouldBe(order);
        refund.Reference.ShouldBe("psp_1");
        refund.Amount.ShouldBe(42.10m);
        refund.Currency.ShouldBe("EUR");
        refund.VoidedAt.ShouldBe(Now);
        refund.DomainEvents.ShouldHaveSingleItem()
            .ShouldBe(new PaymentRefundedDomainEvent(order, "psp_1", 42.10m, "EUR", Now));
    }

    [Fact]
    public void A_declined_intent_has_nothing_to_refund()
    {
        PaymentIntent intent = PaymentIntent.Decline(OrderId.New(), 42.10m, "EUR", "card_declined", Now);

        Should.Throw<DomainException>(() => Refund.Voided(intent, Now),
            "ADR-047: PaymentRefunded means money moved back");
    }
}
