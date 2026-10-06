using Common.Contracts.Ordering.V1;
using Notifications.Application.Contacts;
using Notifications.Application.Delivery;
using Notifications.Application.Records;
using Notifications.Application.Rendering;
using Shouldly;
using Xunit;

namespace Notifications.Application.Tests;

/// <summary>The send worker's decisions, one row per case (ADR-049, ADR-052).</summary>
public class SendRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private static OrderRecord Placed() => OrderRecord.For(Guid.CreateVersion7(), Guid.CreateVersion7(), Now);

    private static OrderRecord Cancelled(string? reason, string? origin)
    {
        OrderRecord record = Placed();
        record.Cancel(reason, origin, Now).ShouldBeTrue();
        return record;
    }

    public static TheoryData<string> EveryKeyButTheDecline() =>
        [.. TemplateKeys.Placeholders.Keys.Where(k => k != TemplateKeys.PaymentDeclined)];

    /// <summary>ADR-049's reading: the workflow's origin, and no other, is not the customer's.</summary>
    public static TheoryData<string?, string?, bool> Readings() => new()
    {
        // The workflow's, whatever the reason.
        { CancelReasons.PaymentDeclined, CancelOrigins.Workflow, false },
        { CancelReasons.PaymentTimeout, CancelOrigins.Workflow, false },
        { CancelReasons.OutOfStock, CancelOrigins.Workflow, false },

        // The customer's, whatever the reason: a caller may assert a payment one (§9.6).
        { CancelReasons.CustomerRequest, CancelOrigins.User, true },
        { CancelReasons.PaymentDeclined, CancelOrigins.User, true },

        // Absent, as an older publisher sends it, or a third a newer one could send: the reason is never read.
        { CancelReasons.PaymentDeclined, null, true },
        { CancelReasons.PaymentTimeout, null, true },
        { CancelReasons.OutOfStock, null, true },
        { CancelReasons.CustomerRequest, null, true },
        { null, null, true },
        { CancelReasons.PaymentDeclined, "system", true },
        { CancelReasons.CustomerRequest, "system", true },
    };

    /// <summary>A stored contact's age in minutes: ADR-052's fifteen fresh, a day served stale.</summary>
    public static TheoryData<int, ContactAge> Ages() => new()
    {
        { 0, ContactAge.Fresh },
        { 14, ContactAge.Fresh },
        { 15, ContactAge.Stale },
        { 60, ContactAge.Stale },
        { (24 * 60) - 1, ContactAge.Stale },
        { 24 * 60, ContactAge.Expired },
        { 3 * 24 * 60, ContactAge.Expired },
    };

    [Theory]
    [MemberData(nameof(EveryKeyButTheDecline))]
    public void A_notice_waits_for_its_order_record_and_for_nothing_more(string key)
    {
        SendRules.AwaitsOrderRecord(key, null).ShouldBeTrue("§9.4 orders nothing between the events");
        SendRules.AwaitsOrderRecord(key, Placed()).ShouldBeFalse();
    }

    [Fact]
    public void A_decline_waits_for_its_order_record_and_then_for_its_cancellation()
    {
        SendRules.AwaitsOrderRecord(TemplateKeys.PaymentDeclined, null).ShouldBeTrue();
        SendRules
            .AwaitsOrderRecord(TemplateKeys.PaymentDeclined, Placed())
            .ShouldBeTrue("every decline has a cancellation, published before or after it (§9.6)");
        SendRules.AwaitsOrderRecord(
                TemplateKeys.PaymentDeclined,
                Cancelled(CancelReasons.PaymentDeclined, CancelOrigins.Workflow))
            .ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(Readings))]
    public void A_decline_is_suppressed_exactly_when_the_customer_cancelled(
        string? reason,
        string? origin,
        bool customers)
    {
        OrderRecord record = Cancelled(reason, origin);

        SendRules.CustomerCancelled(record).ShouldBe(customers);
        SendRules.Suppresses(TemplateKeys.PaymentDeclined, record).ShouldBe(customers);
    }

    [Theory]
    [MemberData(nameof(EveryKeyButTheDecline))]
    public void Nothing_but_a_decline_is_ever_suppressed(string key)
    {
        SendRules
            .Suppresses(key, Cancelled(CancelReasons.CustomerRequest, CancelOrigins.User))
            .ShouldBeFalse("the cancellation itself, and every other notice, goes to the customer who cancelled");
    }

    [Fact]
    public void An_order_nobody_cancelled_was_not_cancelled_by_the_customer()
    {
        SendRules.CustomerCancelled(Placed()).ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(Ages))]
    public void A_stored_contact_s_age_is_read_against_both_numbers(int minutes, ContactAge expected)
    {
        ContactRecord stored = new("aigerim@example.test", "kk", Now.AddMinutes(-minutes));

        SendRules.AgeOf(stored, Now, new ContactOptions()).ShouldBe(expected);
    }

    [Fact]
    public void No_stored_contact_is_absent()
    {
        SendRules.AgeOf(null, Now, new ContactOptions()).ShouldBe(ContactAge.Absent);
    }

    [Fact]
    public void A_contact_fetched_ahead_of_this_host_s_clock_is_fresh()
    {
        // The stored instant is the clock of whichever replica wrote it.
        ContactRecord stored = new("aigerim@example.test", null, Now.AddSeconds(5));

        SendRules.AgeOf(stored, Now, new ContactOptions()).ShouldBe(ContactAge.Fresh);
    }

    [Fact]
    public void The_give_up_age_is_reached_at_its_instant_and_not_before()
    {
        TimeSpan age = TimeSpan.FromDays(1);

        SendRules.HasGivenUp(Now, Now + age - TimeSpan.FromTicks(1), age).ShouldBeFalse();
        SendRules.HasGivenUp(Now, Now + age, age).ShouldBeTrue();
    }
}
