using Microsoft.Extensions.Time.Testing;
using Polly;
using Polly.CircuitBreaker;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>What the pinned breaker keeps across a break, so a hop sizes its window apart from it (§9.7).</summary>
public sealed class BreakerWindowTests
{
    private const int MinimumThroughput = 4;

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero));

    [Theory]
    [InlineData(60, 30)]
    [InlineData(10, 30)]
    public async Task A_breaker_that_closes_starts_a_fresh_window_whether_or_not_its_window_outlived_the_break(
        int samplingSeconds,
        int breakSeconds)
    {
        ResiliencePipeline pipeline = Pipeline(
            TimeSpan.FromSeconds(samplingSeconds),
            TimeSpan.FromSeconds(breakSeconds));

        for (int i = 0; i < MinimumThroughput; i++)
            await RunAsync(pipeline, fail: true);

        (await RunAsync(pipeline, fail: false)).Exception.ShouldBeOfType<BrokenCircuitException>();

        _clock.Advance(TimeSpan.FromSeconds(breakSeconds));
        (await RunAsync(pipeline, fail: false)).Exception.ShouldBeNull("the half-open probe succeeds and closes it");

        // Remembered, the opening failures and this one would be over the ratio and the throughput, and reopen it.
        (await RunAsync(pipeline, fail: true)).Exception.ShouldBeOfType<InvalidOperationException>();

        (await RunAsync(pipeline, fail: false)).Exception.ShouldBeNull("the closed breaker sampled one failure alone");
    }

    private ResiliencePipeline Pipeline(TimeSpan sampling, TimeSpan breakFor) =>
        new ResiliencePipelineBuilder { TimeProvider = _clock }
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                MinimumThroughput = MinimumThroughput,
                SamplingDuration = sampling,
                BreakDuration = breakFor,
                ShouldHandle = new PredicateBuilder().Handle<InvalidOperationException>()
            })
            .Build();

    private static async Task<Outcome<bool>> RunAsync(ResiliencePipeline pipeline, bool fail)
    {
        ResilienceContext context = ResilienceContextPool.Shared.Get(TestContext.Current.CancellationToken);
        try
        {
            return await pipeline.ExecuteOutcomeAsync<bool, bool>(
                (_, failing) => ValueTask.FromResult(failing
                    ? Outcome.FromException<bool>(new InvalidOperationException("down"))
                    : Outcome.FromResult(true)),
                context,
                fail);
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }
}
