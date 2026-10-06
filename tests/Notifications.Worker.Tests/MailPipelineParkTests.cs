using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Notifications.Application.Mail;
using Notifications.Infrastructure.Mail;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>The relay breaker's open state, which the send worker reads to park its claim (§9.7).</summary>
public sealed class MailPipelineParkTests : IDisposable
{
    /// <summary>How long a breaker callback may take to record its break; a deadline, not a sleep.</summary>
    private static readonly TimeSpan CallbackDeadline = TimeSpan.FromSeconds(5);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero));

    private readonly ServiceProvider _services =
        new ServiceCollection().AddMetrics().AddSingleton<MailMetrics>().BuildServiceProvider();

    public void Dispose() => _services.Dispose();

    [Fact]
    public async Task Faults_short_of_the_throughput_park_nothing()
    {
        MailPipeline pipeline = Pipeline();

        await FailAsync(pipeline, MailHop.CircuitBreakerMinimumThroughput - 1);

        pipeline.IsOpen.ShouldBeFalse("a breaker that has not opened parks no claim");
        pipeline.ParkedUntil.ShouldBeNull();
    }

    [Fact]
    public async Task A_breaker_the_relay_opened_parks_until_its_break_has_passed()
    {
        MailPipeline pipeline = Pipeline();

        await FailAsync(pipeline, MailHop.CircuitBreakerMinimumThroughput);

        pipeline.IsOpen.ShouldBeTrue();
        DateTimeOffset until = await ParkedAsync(pipeline);
        until.ShouldBe(_clock.GetUtcNow() + MailHop.CircuitBreakerBreakDuration);

        _clock.Advance(MailHop.CircuitBreakerBreakDuration - TimeSpan.FromSeconds(1));
        pipeline.IsOpen.ShouldBeTrue("the break has a second left");

        _clock.Advance(TimeSpan.FromSeconds(1));
        pipeline.IsOpen.ShouldBeFalse("past the break a pass claims again, and its first send is the probe");
    }

    [Fact]
    public async Task A_probe_that_fails_parks_the_claim_for_another_break()
    {
        MailPipeline pipeline = Pipeline();
        await FailAsync(pipeline, MailHop.CircuitBreakerMinimumThroughput);
        await ParkedAsync(pipeline);
        _clock.Advance(MailHop.CircuitBreakerBreakDuration);

        await FailAsync(pipeline, 1);

        pipeline.IsOpen.ShouldBeTrue();
        (await ParkedAsync(pipeline)).ShouldBe(_clock.GetUtcNow() + MailHop.CircuitBreakerBreakDuration);
    }

    [Fact]
    public async Task A_probe_that_succeeds_ends_the_park()
    {
        MailPipeline pipeline = Pipeline();
        await FailAsync(pipeline, MailHop.CircuitBreakerMinimumThroughput);
        await ParkedAsync(pipeline);
        _clock.Advance(MailHop.CircuitBreakerBreakDuration);

        await pipeline.Pipeline.ExecuteAsync(_ => ValueTask.FromResult(1), TestContext.Current.CancellationToken);

        pipeline.IsOpen.ShouldBeFalse();
        await WaitAsync(() => pipeline.ParkedUntil is null);
    }

    private MailPipeline Pipeline() => new(_services.GetRequiredService<MailMetrics>(), _clock);

    // Rejected, which the retry leaves alone, so each send is one attempt and no delay waits on the held clock.
    private static async Task FailAsync(MailPipeline pipeline, int sends)
    {
        for (int send = 0; send < sends; send++)
        {
            await Should.ThrowAsync<Exception>(async () =>
                await pipeline.Pipeline.ExecuteAsync<int>(
                    _ => throw new MailUnavailableException("Staged.", MailFault.Rejected, 550),
                    TestContext.Current.CancellationToken));
        }
    }

    /// <summary>The break the breaker's callback recorded, once it has run.</summary>
    private static async Task<DateTimeOffset> ParkedAsync(MailPipeline pipeline)
    {
        await WaitAsync(() => pipeline.ParkedUntil is not null);

        return pipeline.ParkedUntil!.Value;
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + CallbackDeadline;

        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException($"The breaker's callback did not run within {CallbackDeadline}.");

            await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);
        }
    }
}
