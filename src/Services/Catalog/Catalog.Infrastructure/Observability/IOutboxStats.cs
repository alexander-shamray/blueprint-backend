using Common.Application;

namespace Catalog.Infrastructure.Observability;

/// <summary>The three questions §13.6's gauges ask of the outbox table, each per lane.</summary>
/// <remarks>The lane is never optional: the two lanes fail differently and need different people (§13.6).</remarks>
public interface IOutboxStats
{
    /// <summary>Seconds since the lane's oldest unprocessed row was raised, or zero when it is empty.</summary>
    double OldestAgeSeconds(OutboxLane lane);

    /// <summary>Unprocessed rows on this lane, abandoned ones included.</summary>
    int PendingCount(OutboxLane lane);

    /// <summary>Unprocessed rows on this lane past §9.4's attempt cap, which will never be delivered.</summary>
    int AbandonedCount(OutboxLane lane);
}
