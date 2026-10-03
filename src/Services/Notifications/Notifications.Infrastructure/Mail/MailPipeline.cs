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
    public MailPipeline(MailMetrics metrics) =>
        Pipeline = new ResiliencePipelineBuilder()
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
                BreakDuration = MailHop.CircuitBreakerBreakDuration
            })
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = MailHop.AttemptTimeout,

                // The one place an attempt timeout before the send is distinguishable from the caller cancelling.
                OnTimeout = _ =>
                {
                    metrics.Unavailable(MailFault.Transient);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();

    public ResiliencePipeline Pipeline { get; }
}
