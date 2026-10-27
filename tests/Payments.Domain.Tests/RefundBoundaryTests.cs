using Common.Domain;
using Payments.Domain.Intents;
using Payments.Domain.Refunds;
using Shouldly;
using Xunit;

namespace Payments.Domain.Tests;

/// <summary>The void guard's two halves, each alone, and the defaults a store materialises.</summary>
public class RefundBoundaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_authorised_intent_without_a_reference_has_nothing_to_refund()
    {
        PaymentIntent intent = Materialised();
        SetPrivately(intent, nameof(PaymentIntent.Status), PaymentIntentStatus.Authorised);

        Should.Throw<DomainException>(() => Refund.Voided(intent, Now))
            .Message.ShouldBe("Only an authorised payment can be refunded.");
    }

    [Fact]
    public void A_declined_intent_that_holds_a_reference_has_nothing_to_refund()
    {
        PaymentIntent intent = Materialised();
        SetPrivately(intent, nameof(PaymentIntent.Status), PaymentIntentStatus.Declined);
        SetPrivately(intent, nameof(PaymentIntent.Reference), "psp_1");

        Should.Throw<DomainException>(() => Refund.Voided(intent, Now))
            .Message.ShouldBe("Only an authorised payment can be refunded.");
    }

    [Fact]
    public void A_refund_a_store_materialises_starts_with_an_empty_reference_and_currency()
    {
        var refund = (Refund)Activator.CreateInstance(typeof(Refund), nonPublic: true)!;

        refund.Reference.ShouldBe("");
        refund.Currency.ShouldBe("");
    }

    [Fact]
    public void An_intent_a_store_materialises_starts_with_an_empty_currency()
    {
        Materialised().Currency.ShouldBe("");
    }

    private static PaymentIntent Materialised() =>
        (PaymentIntent)Activator.CreateInstance(typeof(PaymentIntent), nonPublic: true)!;

    private static void SetPrivately<T>(PaymentIntent intent, string property, T value) =>
        typeof(PaymentIntent).GetProperty(property)!.SetValue(intent, value);
}
