using System.Reflection;
using Notifications.Application.Contacts;
using Shouldly;
using Xunit;

namespace Notifications.Application.Tests;

public class ContactPortTests
{
    [Fact]
    public void A_lookup_is_found_or_no_such_customer_and_nothing_else()
    {
        typeof(ContactLookup).GetNestedTypes()
            .Where(t => t.IsSubclassOf(typeof(ContactLookup)))
            .Select(t => t.Name)
            .ShouldBe(["Found", "NoSuchCustomer"], ignoreOrder: true,
                "a fault is an exception, so an owner that is down can never reach a row as an absence");
    }

    [Fact]
    public void A_found_contact_carries_the_mailbox_and_the_locale_and_nothing_else_the_owner_offered()
    {
        // ADR-052: Keycloak's user representation carries a name and attributes nobody asked for.
        typeof(ContactLookup.Found)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ShouldBe(["Email", "Locale"], ignoreOrder: true);
    }

    [Fact]
    public void A_found_contact_prints_no_mailbox()
    {
        ContactLookup.Found found = new("aigerim@example.test", "kk");

        found.ToString().ShouldNotContain("aigerim", Case.Insensitive, "§13.4: a log takes the ids, never the mailbox");
    }

    [Fact]
    public void A_stored_contact_prints_no_mailbox()
    {
        ContactRecord record = new("aigerim@example.test", "kk", DateTimeOffset.UnixEpoch);

        record.ToString().ShouldNotContain("aigerim", Case.Insensitive);
    }
}
