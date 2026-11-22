using Shouldly;
using Xunit;

namespace Common.Application.Tests;

/// <summary>ADR-212: a value spliced into a path segment is never one the URI resolves away.</summary>
public sealed class PathSegmentTests
{
    [Theory]
    [InlineData("psp_8f2a")]
    [InlineData("...")]
    [InlineData(".a")]
    [InlineData("a/..")]
    public void A_value_that_escapes_to_one_segment_holds(string value)
    {
        PathSegment.Holds(value).ShouldBeTrue();
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData(null)]
    public void A_dot_segment_or_an_empty_value_does_not(string? value)
    {
        PathSegment.Holds(value).ShouldBeFalse();
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public void The_refused_values_are_the_ones_a_uri_resolves_away(string value)
    {
        // The reason for the rule, held here so a runtime that stopped resolving them would be noticed.
        Uri built = new(new Uri("https://provider.test/v1/"), $"device/{Uri.EscapeDataString(value)}/void");

        built.AbsolutePath.ShouldNotContain($"/{value}/");
    }
}
