using Notifications.Application.Push;
using Shouldly;
using Xunit;

namespace Notifications.Application.Tests;

public class PushPortTests
{
    [Fact]
    public void A_push_is_accepted_or_refused_and_nothing_else()
    {
        typeof(PushResult)
            .GetNestedTypes()
            .Where(t => t.IsSubclassOf(typeof(PushResult)))
            .Select(t => t.Name)
            .ShouldBe(
                ["Accepted", "Refused"],
                ignoreOrder: true,
                "a fault is an exception, so a provider's outage can never reach a row as a refusal");
    }

    [Fact]
    public void A_refusal_is_the_tokens_and_each_is_its_own_terminal_reason()
    {
        Enum.GetNames<PushRefusal>().ShouldBe(["Unregistered", "InvalidToken"], ignoreOrder: true);
    }

    [Fact]
    public void A_fault_is_one_of_five_and_the_names_are_the_counters_vocabulary()
    {
        Enum
            .GetNames<PushFault>()
            .ShouldBe(["Transient", "Unconfirmed", "Tls", "Credential", "Rejected"], ignoreOrder: true);
    }

    [Fact]
    public void A_push_prints_its_delivery_and_neither_its_token_nor_its_text()
    {
        OutboundPush push = new(
            "apns",
            "com.example.shop.ios",
            "a1b2c3d4e5f6",
            "Your order is placed",
            "Order 42 is placed.",
            Guid.CreateVersion7(),
            "order-placed",
            Guid.CreateVersion7().ToString("N"),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow);

        string printed = push.ToString();

        printed.ShouldContain(push.DeliveryId.ToString());
        printed.ShouldNotContain("a1b2c3d4e5f6", Case.Insensitive, "ADR-117: a log takes the ids, never the token");
        printed.ShouldNotContain("Order 42", Case.Insensitive, "nor the text, which a lock screen shows");
    }

    [Fact]
    public void An_unavailable_provider_carries_its_cause_and_its_status_and_nothing_of_the_providers_own()
    {
        PushUnavailableException thrown = new("Delivery x met APNs' 503.", PushFault.Transient, 503);

        thrown.Cause.ShouldBe(PushFault.Transient);
        thrown.HttpStatus.ShouldBe(503);
        thrown.InnerException.ShouldBeNull("a provider's own exception can quote the token");
    }
}
