using Common.Application;

namespace Inventory.Infrastructure.Observability;

/// <summary>
/// The three questions §13.6's gauges ask of the outbox table, each per lane.
/// </summary>
/// <remarks>
/// <b>Every member takes the lane, and the predicate is not optional on any of
/// them.</b> The two lanes fail for different reasons, produce different
/// symptoms and need different people (§13.6) — an untagged gauge cannot
/// answer the first question its runbook asks.
/// </remarks>
public interface IOutboxStats
{
    /// <summary>
    /// Seconds since the oldest unprocessed row on this lane was raised, or
    /// zero when the lane is empty. Zero and "healthy" are the same reading
    /// here, which is why <see cref="PendingCount"/> exists beside it.
    /// </summary>
    double OldestAgeSeconds(OutboxLane lane);

    /// <summary>Unprocessed rows on this lane, abandoned ones included.</summary>
    int PendingCount(OutboxLane lane);

    /// <summary>
    /// Unprocessed rows on this lane that have exhausted §9.4's attempt cap and
    /// will therefore never be delivered.
    /// </summary>
    int AbandonedCount(OutboxLane lane);
}
