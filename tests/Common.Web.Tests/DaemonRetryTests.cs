using Common.TestSupport;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

/// <summary>When <see cref="DaemonRetry"/> tries a call again, and when it lets the failure through.</summary>
public class DaemonRetryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_call_refused_short_of_the_last_attempt_is_tried_again_until_it_succeeds()
    {
        int calls = 0;

        await DaemonRetry.RetryAsync(
            _ => ++calls < DaemonRetry.Attempts ? throw new TimeoutException("pipe busy") : Task.CompletedTask,
            TimeSpan.Zero,
            Ct);

        calls.ShouldBe(DaemonRetry.Attempts);
    }

    [Fact]
    public async Task A_call_refused_on_every_attempt_fails_with_the_last_refusal()
    {
        int calls = 0;

        TimeoutException thrown = await Should.ThrowAsync<TimeoutException>(
            () => DaemonRetry.RetryAsync(
                _ => throw new TimeoutException($"attempt {++calls}"),
                TimeSpan.Zero,
                Ct));

        thrown.Message.ShouldBe($"attempt {DaemonRetry.Attempts}");
        calls.ShouldBe(DaemonRetry.Attempts);
    }

    [Fact]
    public async Task A_cancelled_call_is_not_tried_again()
    {
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        int calls = 0;

        await Should.ThrowAsync<OperationCanceledException>(
            () => DaemonRetry.RetryAsync(
                ct =>
                {
                    calls++;
                    ct.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                },
                TimeSpan.Zero,
                cancelled.Token));

        calls.ShouldBe(1);
    }

    [Fact(Timeout = 10_000)]
    public async Task A_call_that_never_finishes_is_abandoned_at_its_budget_on_every_attempt()
    {
        int calls = 0;
        TimeSpan budget = TimeSpan.FromMilliseconds(50);

        TimeoutException thrown = await Should.ThrowAsync<TimeoutException>(
            () => DaemonRetry.RetryBoundedAsync(
                _ =>
                {
                    calls++;
                    return new TaskCompletionSource().Task;
                },
                budget,
                TimeSpan.Zero,
                Ct));

        thrown.Message.ShouldContain(budget.ToString());
        calls.ShouldBe(DaemonRetry.Attempts);
    }

    [Fact(Timeout = 10_000)]
    public async Task A_call_that_stalls_once_succeeds_on_the_next_attempt()
    {
        int calls = 0;

        await DaemonRetry.RetryBoundedAsync(
            _ => ++calls == 1 ? new TaskCompletionSource().Task : Task.CompletedTask,
            TimeSpan.FromMilliseconds(50),
            TimeSpan.Zero,
            Ct);

        calls.ShouldBe(2);
    }

    [Fact(Timeout = 10_000)]
    public async Task An_attempt_past_its_budget_cancels_the_token_the_call_was_given()
    {
        List<CancellationToken> given = [];

        await Should.ThrowAsync<TimeoutException>(
            () => DaemonRetry.RetryBoundedAsync(
                ct =>
                {
                    given.Add(ct);
                    return Task.Delay(Timeout.InfiniteTimeSpan, ct);
                },
                TimeSpan.FromMilliseconds(50),
                TimeSpan.Zero,
                Ct));

        given.Count.ShouldBe(DaemonRetry.Attempts);
        given.ShouldAllBe(ct => ct.IsCancellationRequested, "a cancelled MassTransit start stops its bus");
    }

    [Fact(Timeout = 10_000)]
    public async Task A_bounded_call_cancelled_by_its_caller_is_not_tried_again()
    {
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        int calls = 0;

        await Should.ThrowAsync<OperationCanceledException>(
            () => DaemonRetry.RetryBoundedAsync(
                ct =>
                {
                    calls++;
                    return Task.Delay(Timeout.InfiniteTimeSpan, ct);
                },
                TimeSpan.FromMinutes(1),
                TimeSpan.Zero,
                cancelled.Token));

        calls.ShouldBe(1);
    }
}
