using MassTransit;

namespace Inventory.Infrastructure.Messaging;

/// <summary>
/// §9.8's retry policy: the ladder every receive endpoint applies unless it
/// says otherwise.
/// </summary>
/// <remarks>
/// These are in-memory retries of one broker delivery, not redeliveries.
/// <c>UseMessageRetry</c> holds the message and waits, so the delivery lock and
/// the endpoint's concurrency slot are still taken for the whole ladder — which
/// is what makes the ceiling an operational number rather than only a
/// correctness one. MassTransit's redelivery filters are a different API that
/// releases the message and has the broker deliver it again, and §9.5 uses the
/// word in that sense throughout.
/// <para>
/// Declaring the ladder once is what makes agreement between the endpoints
/// structural: an endpoint that wants a different one has to say so, where a
/// value repeated per endpoint can only be checked by reading every call site
/// and comparing them.
/// </para>
/// <para>
/// What this type does not decide is what gets retried.
/// <c>inventory-commands</c> ignores <c>ContractMappingException</c> before
/// calling <see cref="Standard"/> — a malformed contract does not parse
/// itself no matter how many times it is retried — and that exclusion is
/// the endpoint's, because it is a claim about which faults are terminal
/// rather than about how long to wait between attempts. Folding it in here
/// would apply one endpoint's exclusion to those that never raise it.
/// </para>
/// <para>
/// <see cref="RetryLimit"/> counts retries and not deliveries: the endpoint
/// makes one more attempt than this number, and the wait it produces is that
/// many intervals. §9.6's confirmation wait has to clear that sum — a floor it
/// must exceed rather than the term that decides it, which is §9.4's
/// dispatcher backoff.
/// </para>
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
