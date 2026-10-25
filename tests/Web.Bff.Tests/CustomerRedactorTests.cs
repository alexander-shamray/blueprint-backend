using System.Diagnostics;
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
    [InlineData("ref 1234 4111 1111 1111 1111 paid", CustomerRedactor.Card)]
    public void A_contact_or_card_detail_is_replaced_by_its_marker(string text, string marker)
    {
        string redacted = _redactor.Redact(text, Customer);

        redacted.ShouldContain(marker);
        redacted.ShouldNotContain("1111 1111");
        redacted.ShouldNotContain("@example");
        redacted.ShouldNotContain("7946");
        redacted.ShouldNotContain("5555-5555");
    }

    // A client tool's result is redacted at up to the body limit, so a run of many groups must cost its length.
    [Fact]
    public void A_long_run_of_groups_is_redacted_in_time_proportional_to_its_length()
    {
        string run = string.Join(' ', Enumerable.Repeat("1", 65_000));
        Stopwatch clock = Stopwatch.StartNew();

        _redactor.Redact(run, Customer);

        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Every_card_in_one_run_of_groups_is_replaced()
    {
        _redactor.Redact("cards 4111 1111 1111 1111 5555 5555 5555 4444 thanks", Customer)
            .ShouldBe($"cards {CustomerRedactor.Card} {CustomerRedactor.Card} thanks");
    }

    [Theory]
    [InlineData("order 12345678-1234-1234-1234-123456789012 is late")]
    [InlineData("order 0199a2b4-5c6d-7e8f-9a0b-1c2d3e4f5a6b is late")]
    [InlineData("the total was 1234567890123.45 GBP")]
    [InlineData("tracking number 1234567890123456")]
    [InlineData("reference 12345678901234567894 passes the check but is no card")]
    [InlineData("it cost 49.99 and arrived on 2026-10-03")]
    public void An_order_s_ids_amounts_dates_and_a_number_that_fails_luhn_are_left_alone(string text)
    {
        _redactor.Redact(text, Customer).ShouldBe(text);
    }
}
