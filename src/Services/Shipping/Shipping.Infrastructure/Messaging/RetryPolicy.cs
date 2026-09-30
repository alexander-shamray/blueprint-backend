using MassTransit;

namespace Shipping.Infrastructure.Messaging;

/// <summary>§9.8's retry policy: the ladder every receive endpoint applies unless it says otherwise.</summary>
/// <remarks>In-memory retries that hold the endpoint's concurrency slot for the whole ladder (§9.8).</remarks>
internal static class RetryPolicy
{
    /// <summary>Retries after the first attempt, so one more attempt than this.</summary>
    public const int RetryLimit = 5;

    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

    /// <summary>The ceiling the ladder climbs towards; it is not reached at <see cref="RetryLimit"/> retries.</summary>
    public static readonly TimeSpan MaxInterval = TimeSpan.FromMinutes(1);

    public static readonly TimeSpan IntervalDelta = TimeSpan.FromSeconds(2);

    public static void Standard(IRetryConfigurator retry) =>
        retry.Exponential(RetryLimit, MinInterval, MaxInterval, IntervalDelta);
}
