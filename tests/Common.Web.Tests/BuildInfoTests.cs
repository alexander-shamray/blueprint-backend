using Shouldly;
using Xunit;

namespace Common.Web.Tests;

public class BuildInfoTests
{
    [Theory]
    [InlineData("1.2.3+0a1b2c3", "1.2.3")]          // deterministic build
    [InlineData("1.2.3", "1.2.3")]                  // no source revision
    [InlineData("1.2.3+", "1.2.3")]                 // suffix marker, empty sha
    [InlineData(null, "0.0.0")]                     // no attribute at all
    [InlineData("", "0.0.0")]
    [InlineData("   ", "0.0.0")]
    public void The_informational_version_is_normalised(string? informational, string expected)
    {
        // Through Normalise, because a test can choose what goes in.
        BuildInfo.Normalise(informational).ShouldBe(expected);
    }

    [Fact]
    public void The_entry_assembly_supplies_a_version()
    {
        // The SDK stamps "<version>+<sha>" inside a git working copy, so this strips a genuine input.
        BuildInfo.Version.ShouldNotBeNullOrWhiteSpace();
        BuildInfo.Version.ShouldNotContain("+");
    }
}
