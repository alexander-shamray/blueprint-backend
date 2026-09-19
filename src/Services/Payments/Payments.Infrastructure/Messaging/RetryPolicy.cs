using MassTransit;

namespace Payments.Infrastructure.Messaging;

/// <summary>
/// §9.8's retry policy: the ladder every receive endpoint applies unless it
/// says otherwise.
/// </summary>
/// <remarks>
/// These are in-memory retries of one broker delivery, not redeliveries:
/// <c>UseMessageRetry</c> holds the message and the endpoint's concurrency
/// slot for the whole ladder, which is what makes the ceiling an operational
/// number and not only a correctness one.
/// </remarks>
internal static class RetryPolicy
{
    /// <summary>
    /// How many retries follow the first attempt, so the endpoint makes one
    /// more attempt than this and waits this many intervals.
    /// </summary>
    public const int RetryLimit = 5;

    /// <summary>The first interval, before any doubling.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The ceiling the ladder climbs towards. It is not reached at
    /// <see cref="RetryLimit"/> retries, which is the arithmetic §9.6's
    /// confirmation wait depends on.
    /// </summary>
    public static readonly TimeSpan MaxInterval = TimeSpan.FromMinutes(1);

    /// <summary>What each interval adds on top of the doubling.</summary>
    public static readonly TimeSpan IntervalDelta = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Applies the policy to one endpoint's retry configurator.
    /// </summary>
    public static void Standard(IRetryConfigurator retry) =>
        retry.Exponential(RetryLimit, MinInterval, MaxInterval, IntervalDelta);
}
