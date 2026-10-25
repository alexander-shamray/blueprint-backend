using Common.Assistant;
using Shouldly;
using Web.Bff.Assistant;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>ADR-151's data policy: contact and card details are replaced, and an order's facts are not.</summary>
public sealed class CustomerRedactorTests
{
    private static readonly Caller Customer = new("customer", "customer");

    private readonly CustomerRedactor _redactor = new();

    [Theory]
    [InlineData("mail me at jo.bloggs+shop@example.co.uk please", CustomerRedactor.Email)]
    [InlineData("my number is +44 (20) 7946-0958", CustomerRedactor.Phone)]
    [InlineData("I paid with 4111 1111 1111 1111 yesterday", CustomerRedactor.Card)]
    [InlineData("card 5555-5555-5555-4444", CustomerRedactor.Card)]
    [InlineData("card 4111 1111 1111 1111 123 is the one", CustomerRedactor.Card)]
    [InlineData("card 4111 1111 1111 1111 12/27", CustomerRedactor.Card)]
    public void A_contact_or_card_detail_is_replaced_by_its_marker(string text, string marker)
    {
        string redacted = _redactor.Redact(text, Customer);

        redacted.ShouldContain(marker);
        redacted.ShouldNotContain("1111 1111");
        redacted.ShouldNotContain("@example");
        redacted.ShouldNotContain("7946");
        redacted.ShouldNotContain("5555-5555");
    }

    [Theory]
    [InlineData("order 12345678-1234-1234-1234-123456789012 is late")]
    [InlineData("order 0199a2b4-5c6d-7e8f-9a0b-1c2d3e4f5a6b is late")]
    [InlineData("the total was 1234567890123.45 GBP")]
    [InlineData("tracking number 1234567890123456")]
    [InlineData("it cost 49.99 and arrived on 2026-10-03")]
    public void An_order_s_ids_amounts_dates_and_a_number_that_fails_luhn_are_left_alone(string text)
    {
        _redactor.Redact(text, Customer).ShouldBe(text);
    }
}
