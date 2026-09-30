using Shouldly;
using Xunit;

namespace Gateway.Api.Tests;

/// <summary>§10.3's <c>Retry-After</c> rounding, on the sub-second input no request here can reach.</summary>
public sealed class RetryAfterHeaderTests
{
    [Theory]
    // Truncation would say "retry now" while the limiter is still refusing.
    [InlineData(0.2, 1)]
    [InlineData(0.8, 1)]
    // Whole seconds are unchanged, or every 429 advertises a second it does not need.
    [InlineData(1.0, 1)]
    [InlineData(59.0, 59)]
    [InlineData(59.2, 60)]
    // An expired lease: zero is truthful, and a negative is a header no client can parse.
    [InlineData(0.0, 0)]
    [InlineData(-0.5, 0)]
    public void Whole_seconds_are_rounded_up_never_down(double remaining, int expected) =>
        RetryAfterHeader.Seconds(TimeSpan.FromSeconds(remaining)).ShouldBe(expected);
}
