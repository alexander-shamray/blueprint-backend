using System.Reflection;
using Common.TestSupport;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>docs/personal-data.md names every retention window this host's types hold (§11.7).</summary>
public class RetentionMapTests
{
    private static readonly Type[] Declared = [.. RetentionMapRule.BuildingBlocks];

    private static readonly Assembly Host = typeof(RetentionMapTests).Assembly;

    private static readonly string[] Prefixes = ["Ordering.", "Common."];

    private static readonly NotRetention[] Excluded = [.. RetentionMapRule.BuildingBlocksNotRetention];

    [Fact]
    public void Every_retention_window_this_host_holds_is_named_in_the_map()
    {
        RetentionMapRule.Offenders(Declared, Excluded).ShouldBeEmpty();
    }

    [Fact]
    public void The_rule_above_is_looking_at_this_hosts_windows()
    {
        // The floor: an offender list is as green over a declared set that holds nothing, or a map never read.
        RetentionMapRule.Members(Declared).ShouldNotBeEmpty();
        RetentionMapRule.Offenders(Declared, Excluded, map: string.Empty).ShouldNotBeEmpty();
        RetentionMapRule.Undeclared(Host, [], Prefixes).ShouldNotBeEmpty();
    }

    [Fact]
    public void Every_type_this_host_holds_that_bears_a_window_by_name_is_declared()
    {
        RetentionMapRule.Undeclared(Host, Declared, Prefixes).ShouldBeEmpty();
    }
}
