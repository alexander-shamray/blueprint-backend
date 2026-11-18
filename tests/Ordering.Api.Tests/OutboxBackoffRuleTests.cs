using Common.TestSupport;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>The backoff rule the dispatcher suites read, held to telling one rung from the next.</summary>
public class OutboxBackoffRuleTests
{
    [Fact]
    public void A_first_failure_backed_off_for_the_next_rung_is_refused()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        OutboxBackoffRule.Violation(now + OutboxBackoffRule.Ladder(0), attemptsBefore: 0, now).ShouldBeNull();
        OutboxBackoffRule.Violation(now + OutboxBackoffRule.Ladder(1), attemptsBefore: 0, now).ShouldNotBeNull();
    }

    [Fact]
    public void A_later_failure_keeps_the_full_slack_below_and_only_rounding_above()
    {
        TimeSpan expected = OutboxBackoffRule.Ladder(3);
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        OutboxBackoffRule.Violation(now + expected - TimeSpan.FromSeconds(7), attemptsBefore: 3, now).ShouldBeNull();
        OutboxBackoffRule.Violation(now + expected + TimeSpan.FromSeconds(1), attemptsBefore: 3, now).ShouldBeNull();
        OutboxBackoffRule.Violation(now + expected + TimeSpan.FromSeconds(7), attemptsBefore: 3, now).ShouldNotBeNull();
        OutboxBackoffRule.Violation(now + OutboxBackoffRule.Ladder(4), attemptsBefore: 3, now).ShouldNotBeNull();
    }

    [Fact]
    public void A_row_with_no_lock_is_refused()
    {
        OutboxBackoffRule.Violation(lockedUntil: null, attemptsBefore: 0, DateTimeOffset.UtcNow).ShouldNotBeNull();
    }
}
