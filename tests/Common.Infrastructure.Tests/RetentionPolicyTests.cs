using Common.Application;
using Common.Infrastructure.Messaging;
using Shouldly;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>§9.5's policy is caller-supplied, so a wrong setting is refused rather than held.</summary>
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
        Should.Throw<ArgumentOutOfRangeException>(
            () => new RetentionPolicy { IdempotencyWindow = IdempotencyRetention.Window - OneSecond });

        // Equal is admitted (ADR-038); the floor is read, not restated.
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

        // Refused as negative rather than as below the floor, which is the message an operator reads.
        Should.Throw<ArgumentOutOfRangeException>(
            () => new RetentionPolicy { IdempotencyWindow = TimeSpan.FromDays(-1) });
    }

    [Fact]
    public void The_default_marker_window_satisfies_the_floor_the_init_enforces()
    {
        // A field initialiser bypasses the init's floor, and the default is what services ship.
        new RetentionPolicy()
            .IdempotencyWindow
            .ShouldBeGreaterThanOrEqualTo(
                IdempotencyRetention.MarkerFloor,
                "the default is the one value the init validator never sees");
    }

    [Fact]
    public void A_batch_or_a_ceiling_of_zero_would_disable_retention_in_silence()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new RetentionPolicy { BatchSize = 0 });
        Should.Throw<ArgumentOutOfRangeException>(() => new RetentionPolicy { MaxBatchesPerPass = 0 });
        Should.Throw<ArgumentOutOfRangeException>(() => new RetentionPolicy { BatchSize = -1 });
    }

    [Fact]
    public void A_non_positive_interval_is_refused_where_it_can_still_be_read()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new RetentionPolicy { Interval = TimeSpan.Zero });
    }

    [Fact]
    public void A_value_too_large_to_run_is_refused_as_well_as_one_too_small()
    {
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
        // Constructed rather than compared to a constant, so a framework change fails here, not in a host.
        RetentionPolicy policy = new() { Interval = TimeSpan.FromMilliseconds(uint.MaxValue - 1) };

        using PeriodicTimer timer = new(policy.Interval);

        timer.ShouldNotBeNull();
    }

    [Fact]
    public void The_largest_accepted_window_still_gives_a_representable_cutoff()
    {
        RetentionPolicy policy = new() { OutboxWindow = TimeSpan.FromDays(3650) };

        Should.NotThrow(() => DateTimeOffset.UtcNow - policy.OutboxWindow);
    }

    [Fact]
    public void The_refusal_names_the_setting_that_was_wrong()
    {
        ArgumentOutOfRangeException thrown = Should.Throw<ArgumentOutOfRangeException>(
            () => new RetentionPolicy { MaxBatchesPerPass = 0 });

        thrown.ParamName.ShouldBe(nameof(RetentionPolicy.MaxBatchesPerPass));
    }
}
