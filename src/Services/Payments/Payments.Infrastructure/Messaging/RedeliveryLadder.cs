namespace Payments.Infrastructure.Messaging;

/// <summary>
/// §3.2's wait for an <c>OrderPlaced</c> that has not arrived: redelivery, not
/// retry, so the message is released between attempts rather than holding an
/// endpoint slot for minutes.
/// </summary>
/// <remarks>
/// Its total must reach the saga's payment timeout, so a lost order
/// compensates rather than pages. That timeout is Ordering's (§9.6) and
/// unreadable here (§4.2), so the two are held together outside both.
/// </remarks>
public static class RedeliveryLadder
{
    public static IReadOnlyList<TimeSpan> Intervals { get; } =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(4),
        TimeSpan.FromMinutes(8)
    ];

    public static TimeSpan Total { get; } = Intervals.Aggregate(TimeSpan.Zero, (sum, next) => sum + next);
}
