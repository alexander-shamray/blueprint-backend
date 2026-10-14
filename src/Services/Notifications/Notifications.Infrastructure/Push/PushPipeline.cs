using Notifications.Application.Push;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace Notifications.Infrastructure.Push;

/// <summary>One hop's numbers, so both providers build one pipeline shape from their own budget.</summary>
internal sealed record PushHopBudget(
    TimeSpan AttemptTimeout,
    int MaxRetryAttempts,
    TimeSpan RetryDelay,
    TimeSpan MaxRetryDelay,
    TimeSpan TotalTimeout,
    double FailureRatio,
    int MinimumThroughput,
    TimeSpan SamplingDuration,
    TimeSpan BreakDuration);

/// <summary>A provider's budget as one pipeline, held by its singleton adapter so the breaker is the host's.</summary>
/// <remarks>
/// Built by hand as <c>MailPipeline</c> is, in the standard handler's order: total, retry, breaker, attempt (§9.7).
/// The standard handler is not used because it retries an attempt that timed out, and a push once started may have
/// been delivered; here only a request the provider took nothing in is retried.
/// </remarks>
internal sealed class PushPipeline
{
    private readonly TimeProvider _clock;
    private readonly CircuitBreakerStateProvider _state = new();

    // UTC ticks until which the breaker's break runs, or zero before a break is recorded.
    private long _parkedUntil;

    public PushPipeline(string provider, PushHopBudget hop, PushMetrics metrics, TimeProvider clock)
    {
        _clock = clock;

        Pipeline = new ResiliencePipelineBuilder { TimeProvider = clock }
            .AddTimeout(hop.TotalTimeout)
            .AddRetry(new RetryStrategyOptions
            {
                // A timeout escapes an attempt only before its request started; the adapter turns a later one into
                // an unconfirmed fault, which is never retried (§9.7).
                ShouldHandle = new PredicateBuilder()
                    .Handle<PushUnavailableException>(e => e.Cause == PushFault.Transient)
                    .Handle<TimeoutRejectedException>(),
                MaxRetryAttempts = hop.MaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = hop.RetryDelay,
                MaxDelay = hop.MaxRetryDelay
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                // Every fault, the credential's included: a provider refusing all is met once a window.
                ShouldHandle = new PredicateBuilder()
                    .Handle<PushUnavailableException>()
                    .Handle<TimeoutRejectedException>(),
                FailureRatio = hop.FailureRatio,
                MinimumThroughput = hop.MinimumThroughput,
                SamplingDuration = hop.SamplingDuration,
                BreakDuration = hop.BreakDuration,
                StateProvider = _state,

                // The break's end, which a send loop parks this platform's rows until; the state alone never lapses.
                OnOpened = args =>
                {
                    Interlocked.Exchange(ref _parkedUntil, (clock.GetUtcNow() + args.BreakDuration).UtcTicks);
                    return ValueTask.CompletedTask;
                },
                OnHalfOpened = _ => Unpark(),
                OnClosed = _ => Unpark()
            })
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = hop.AttemptTimeout,

                // Only Polly tells an attempt's own timeout from the total's, so an attempt's is counted here.
                OnTimeout = _ =>
                {
                    metrics.Unavailable(provider, PushFault.Transient);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    public ResiliencePipeline Pipeline { get; }

    /// <summary>When the open breaker's break ends, or null while none is recorded.</summary>
    public DateTimeOffset? ParkedUntil
    {
        get
        {
            long ticks = Interlocked.Read(ref _parkedUntil);

            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>Open until its break passes; then a pass claims, and its first send is the probe (§9.7).</summary>
    public bool IsOpen =>
        (_state.CircuitState is CircuitState.Open or CircuitState.Isolated) &&
        (ParkedUntil is not { } until || _clock.GetUtcNow() < until);

    private ValueTask Unpark()
    {
        Interlocked.Exchange(ref _parkedUntil, 0);
        return ValueTask.CompletedTask;
    }
}
