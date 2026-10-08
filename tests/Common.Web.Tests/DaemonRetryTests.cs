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
}
