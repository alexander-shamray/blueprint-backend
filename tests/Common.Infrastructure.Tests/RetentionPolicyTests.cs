using Common.Application;
using Common.Infrastructure.Messaging;
using Shouldly;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>
/// The policy is service-configurable by design — §9.5 tells the reader to
/// check the inbox window against their broker's redelivery limits — so it is
/// caller-supplied, and what is caller-supplied has to be a value the type
/// refuses to hold wrongly. <c>OutboxTable</c>'s principle, applied to the
/// other registered value.
/// </summary>
/// <remarks>
/// Every one of these settings fails <em>quietly</em> when it is non-positive,
/// which is why they are refused rather than clamped or logged. A negative
/// window puts the cutoff in the future and deletes the rows just written; a
/// zero batch size or ceiling turns every pass into a no-op and stops retention
/// with nothing to see. Only the interval is loud, and it is loud on a
/// background thread inside a host that has already reported ready.
/// </remarks>
public class RetentionPolicyTests
{
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    [Fact]
    public void The_defaults_are_the_documented_ones()
    {
        RetentionPolicy policy = new();

        policy.OutboxWindow.ShouldBe(TimeSpan.FromDays(7));
        policy.InboxWindow.ShouldBe(TimeSpan.FromDays(7));
        policy.IdempotencyWindow.ShouldBe(TimeSpan.FromDays(7));
        policy.BatchSize.ShouldBe(5000);
        policy.Interval.ShouldBe(TimeSpan.FromHours(1));
        policy.MaxBatchesPerPass.ShouldBe(20);
    }

    [Fact]
    public void A_window_in_the_past_is_the_only_direction_that_means_anything()
    {
        // The cutoff is `now - window`. A negative one puts it in the future,
        // so the delete matches every row including the ones written a second
        // ago — for the inbox, silently disabling deduplication at the moment
        // it is most needed.
        Should.Throw<ArgumentOutOfRangeException>(
            () => new RetentionPolicy { OutboxWindow = TimeSpan.FromDays(-1) });
        Should.Throw<ArgumentOutOfRangeException>(
            () => new RetentionPolicy { InboxWindow = TimeSpan.FromDays(-1) });
        Should.Throw<ArgumentOutOfRangeException>(
            () => new RetentionPolicy { InboxWindow = TimeSpan.Zero });
    }

    [Fact]
    public void The_marker_window_cannot_be_shorter_than_the_claim_it_backs_up()
    {
        // §8.5's Redis claim expires; the marker is what refuses a retry after
        // that. A window below the claim's asks for a guarantee shorter than
        // the claim already gives, and since ADR-039 that is a setting that
        // cannot do what it says rather than one that re-opens the duplicate:
        // the purge deletes a marker only once the claim behind its key is
        // gone, so a shorter number is one nothing acts on. Refused either way,
        // and the reason moved — which is why the assertions below are about
        // the relationship rather than about what a wrong value would cost.
        Should.Throw<ArgumentOutOfRangeException>(
            () => new RetentionPolicy { IdempotencyWindow = IdempotencyRetention.Window - OneSecond });

        // Equal is admitted, and that is no promise of no gap: what is left is the marker's INSERT
        // committing inside the claim's own window (§8.5, ADR-039). The floor is read, not restated.
        new RetentionPolicy { IdempotencyWindow = IdempotencyRetention.Window }
            .IdempotencyWindow
            .ShouldBe(IdempotencyRetention.Window);

        IdempotencyRetention.MarkerFloor.ShouldBe(
            IdempotencyRetention.Window,
            "the floor carried an allowance for two terms that are now closed");

        Should.Throw<ArgumentOutOfRangeException>(
            () => new RetentionPolicy { IdempotencyWindow = IdempotencyRetention.MarkerFloor - OneSecond });

        // The floor itself is admitted, and is the smallest window that is.
        new RetentionPolicy { IdempotencyWindow = IdempotencyRetention.MarkerFloor }
            .IdempotencyWindow
            .ShouldBe(IdempotencyRetention.MarkerFloor);

        // And the floor does not replace the other checks: a negative window is
        // still refused as one rather than as a value below the floor, which is
        // the message an operator reads.
        Should.Throw<ArgumentOutOfRangeException>(
            () => new RetentionPolicy { IdempotencyWindow = TimeSpan.FromDays(-1) });
    }

    [Fact]
    public void The_default_marker_window_satisfies_the_floor_the_init_enforces()
    {
        // The floor above is enforced by an `init`, and a field initialiser
        // does not go through one — which is the shape every window here has,
        // and harmless for the other two because they answer to nothing. This
        // one does. Both services register `new RetentionPolicy()`, so the
        // default IS the shipped value and the only value that never meets the
        // validator; raising IdempotencyRetention.Window past seven days —
        // which is the tuning that type exists to make possible — would ship
        // every service a policy violating its own floor, with the test above
        // still green because it only ever exercises the explicit path.
        new RetentionPolicy()
            .IdempotencyWindow
            .ShouldBeGreaterThanOrEqualTo(
                IdempotencyRetention.MarkerFloor,
                "the default is the one value the init validator never sees");
    }

    [Fact]
    public void A_batch_or_a_ceiling_of_zero_would_disable_retention_in_silence()
    {
        // `DELETE TOP (0)` deletes nothing and reports success; a ceiling of
        // zero skips the loop entirely. Either way every pass returns zero, the
        // tables grow, and the only symptom is a number nobody is watching.
        Should.Throw<ArgumentOutOfRangeException>(() => new RetentionPolicy { BatchSize = 0 });
        Should.Throw<ArgumentOutOfRangeException>(() => new RetentionPolicy { MaxBatchesPerPass = 0 });
        Should.Throw<ArgumentOutOfRangeException>(() => new RetentionPolicy { BatchSize = -1 });
    }

    [Fact]
    public void A_non_positive_interval_is_refused_where_it_can_still_be_read()
    {
        // PeriodicTimer throws on this too — from ExecuteAsync, on a background
        // thread, in a host that has already reported ready. Refusing it at the
        // registration puts the failure where somebody is looking.
        Should.Throw<ArgumentOutOfRangeException>(() => new RetentionPolicy { Interval = TimeSpan.Zero });
    }

    [Fact]
    public void A_value_too_large_to_run_is_refused_as_well_as_one_too_small()
    {
        // Positive was not enough, and both directions fail out of sight.
        // `PeriodicTimer` rejects a period above uint.MaxValue - 1
        // milliseconds — verified, about 49.7 days — and it does so from
        // ExecuteAsync, on a background thread, in a host that has already
        // reported ready. A window large enough to make `now - window`
        // unrepresentable throws inside PurgeAsync instead, where the caller
        // logs and swallows: a purge that never runs, once an hour, quietly.
        Should.Throw<ArgumentOutOfRangeException>(
            () => new RetentionPolicy { Interval = TimeSpan.MaxValue });
        Should.Throw<ArgumentOutOfRangeException>(
            () => new RetentionPolicy { Interval = TimeSpan.FromDays(50) });
        Should.Throw<ArgumentOutOfRangeException>(
            () => new RetentionPolicy { OutboxWindow = TimeSpan.MaxValue });
        Should.Throw<ArgumentOutOfRangeException>(
            () => new RetentionPolicy { InboxWindow = TimeSpan.FromDays(3651) });
    }

    [Fact]
    public void The_largest_accepted_interval_is_one_PeriodicTimer_takes()
    {
        // The bound is only right if it is the consumer's own. Constructed
        // here rather than asserted against a constant, so a framework change
        // to that limit fails this test rather than the running host.
        RetentionPolicy policy = new() { Interval = TimeSpan.FromMilliseconds(uint.MaxValue - 1) };

        using PeriodicTimer timer = new(policy.Interval);

        timer.ShouldNotBeNull();
    }

    [Fact]
    public void The_largest_accepted_window_still_gives_a_representable_cutoff()
    {
        // Same test from the other side: the window is spent as `now - window`
        // in PurgeAsync, so the maximum this type accepts has to be one that
        // subtraction survives.
        RetentionPolicy policy = new() { OutboxWindow = TimeSpan.FromDays(3650) };

        Should.NotThrow(() => DateTimeOffset.UtcNow - policy.OutboxWindow);
    }

    [Fact]
    public void The_refusal_names_the_setting_that_was_wrong()
    {
        // Five settings of two shapes: a message saying only "must be positive"
        // would leave the reader to find which one, and the guard is shared.
        ArgumentOutOfRangeException thrown = Should.Throw<ArgumentOutOfRangeException>(
            () => new RetentionPolicy { MaxBatchesPerPass = 0 });

        thrown.ParamName.ShouldBe(nameof(RetentionPolicy.MaxBatchesPerPass));
    }
}
