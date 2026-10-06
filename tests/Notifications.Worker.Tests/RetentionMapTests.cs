using System.Reflection;
using Common.TestSupport;
using Notifications.Application.Contacts;
using Notifications.Infrastructure.Delivery;
using Notifications.Infrastructure.Jurisdiction;
using Notifications.Infrastructure.Retention;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>docs/personal-data.md names every retention window this host's types hold (§11.7).</summary>
public class RetentionMapTests
{
    private static readonly Type[] Declared =
    [
        .. RetentionMapRule.BuildingBlocks,
        typeof(NotificationsJurisdictionOptions),
        typeof(ContactOptions),
        typeof(DeliveryOptions),
        typeof(NotificationsRetentionService)
    ];

    private static readonly Assembly Host = typeof(RetentionMapTests).Assembly;

    private static readonly string[] Prefixes = ["Notifications.", "Common."];

    private static readonly NotRetention[] Excluded =
    [
        .. RetentionMapRule.BuildingBlocksNotRetention,
        new(typeof(ContactOptions), nameof(ContactOptions.Freshness), "when a row is asked again, not its lifetime"),
        new(typeof(NotificationsRetentionService), nameof(NotificationsRetentionService.Interval), "the purge's pacing")
    ];

    [Fact]
    public void Every_retention_window_this_host_holds_is_named_in_the_map()
    {
        RetentionMapRule.Offenders(Declared, Excluded).ShouldBeEmpty();
    }

    [Fact]
    public void A_map_row_naming_a_member_its_type_no_longer_holds_is_caught()
    {
        string map = string.Join(' ', RetentionMapRule.Members(Declared).Select(member => $"`{member}`"));

        RetentionMapRule.Offenders(Declared, Excluded, map).ShouldBeEmpty();
        RetentionMapRule.Offenders(Declared, Excluded, $"{map} `DeliveryOptions.Renamed` `Elsewhere.Renamed`")
            .ShouldHaveSingleItem()
            .ShouldContain("names DeliveryOptions.Renamed");
    }

    [Fact]
    public void The_rule_above_is_looking_at_this_hosts_windows()
    {
        // The floor: an offender list is as green over a declared set that holds nothing, a map never read, or a
        // prefix naming no assembly this host reaches.
        RetentionMapRule.Members(Declared).ShouldNotBeEmpty();
        RetentionMapRule.Offenders(Declared, Excluded, map: string.Empty).ShouldNotBeEmpty();
        RetentionMapRule.Undeclared(Host, [], Prefixes).ShouldNotBeEmpty();
        RetentionMapRule.Unreached(Host, Prefixes).ShouldBeEmpty();
    }

    [Fact]
    public void Every_type_this_host_holds_that_bears_a_window_by_name_is_declared()
    {
        RetentionMapRule.Undeclared(Host, Declared, Prefixes).ShouldBeEmpty();
    }
}
