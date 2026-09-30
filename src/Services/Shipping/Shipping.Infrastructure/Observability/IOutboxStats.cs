using Common.Application;

namespace Shipping.Infrastructure.Observability;

/// <summary>The three questions §13.6's gauges ask of the outbox table, each per lane.</summary>
/// <remarks>The lane is not optional: the two lanes fail differently and need different people (§13.6).</remarks>
public interface IOutboxStats
{
    /// <summary>Seconds since the oldest unprocessed row, or zero when the lane is empty.</summary>
    double OldestAgeSeconds(OutboxLane lane);

    /// <summary>Unprocessed rows on this lane, abandoned ones included.</summary>
    int PendingCount(OutboxLane lane);

    /// <summary>Unprocessed rows past §9.4's attempt cap, which will never be delivered.</summary>
    int AbandonedCount(OutboxLane lane);
}
