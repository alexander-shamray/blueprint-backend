using Notifications.Application.Mail;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace Notifications.Infrastructure.Mail;

/// <summary><see cref="MailHop"/>'s budget as one pipeline, a singleton so the breaker's state is the host's.</summary>
/// <remarks>
/// Built by hand because SMTP is no <c>HttpClient</c>, in the standard handler's order: total, retry, breaker,
/// attempt (§9.7). Polly is the engine under that handler too, so the two hops fail alike.
/// </remarks>
internal sealed class MailPipeline
{
    private readonly TimeProvider _clock;
    private readonly CircuitBreakerStateProvider _state = new();

    // UTC ticks until which the breaker's break runs, or zero before a break is recorded.
    private long _parkedUntil;

    public MailPipeline(MailMetrics metrics, TimeProvider clock)
    {
        _clock = clock;

        Pipeline = new ResiliencePipelineBuilder { TimeProvider = clock }
            .AddTimeout(MailHop.TotalTimeout)
            .AddRetry(new RetryStrategyOptions
            {
                // Only an attempt the relay never took the message in: a retry after that could send it twice (§9.7).
                ShouldHandle = new PredicateBuilder()
                    .Handle<MailUnavailableException>(e => e.Cause == MailFault.Transient)
                    .Handle<TimeoutRejectedException>(),
                MaxRetryAttempts = MailHop.MaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = MailHop.RetryDelay,
                MaxDelay = MailHop.MaxRetryDelay
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                // Every fault, the refusals that are decisions included: a relay refusing all is met once a window.
                ShouldHandle = new PredicateBuilder()
                    .Handle<MailUnavailableException>()
                    .Handle<TimeoutRejectedException>(),
                FailureRatio = MailHop.CircuitBreakerFailureRatio,
                MinimumThroughput = MailHop.CircuitBreakerMinimumThroughput,
                SamplingDuration = MailHop.CircuitBreakerSamplingDuration,
                BreakDuration = MailHop.CircuitBreakerBreakDuration,
                StateProvider = _state,

                // The break's end, which the send worker parks its claim until; the state alone never lapses.
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
                Timeout = MailHop.AttemptTimeout,

                // Only Polly tells an attempt's own timeout from the total's, so an attempt's is counted here.
                OnTimeout = _ =>
                {
                    metrics.Unavailable(MailFault.Transient);
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
        (_state.CircuitState is CircuitState.Open or CircuitState.Isolated)
        && (ParkedUntil is not { } until || _clock.GetUtcNow() < until);

    private ValueTask Unpark()
    {
        Interlocked.Exchange(ref _parkedUntil, 0);
        return ValueTask.CompletedTask;
    }
}
