using System.Diagnostics.Metrics;

namespace Common.Infrastructure.Messaging;

/// <summary>§13.3's messaging instruments, on the <c>Commerce.Messaging</c> meter.</summary>
/// <remarks>Both lags compare a timestamp made on another machine, so §13.7 targets them in seconds.</remarks>
public sealed class MessagingMetrics
{
    /// <summary>Seconds-scale bounds, which the SDK defaults only for instruments it knows by name (§13.3).</summary>
    private static readonly InstrumentAdvice<double> LagBuckets = new()
    {
        HistogramBucketBoundaries = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 30]
    };

    private readonly Histogram<double> _deliveryLag;
    private readonly Histogram<double> _projectionLag;
    private readonly Counter<long> _rejected;
    private readonly Counter<long> _suppressed;

    public MessagingMetrics(IMeterFactory factory)
    {
        Meter meter = factory.Create("Commerce.Messaging");

        _deliveryLag = meter.CreateHistogram<double>(
            "messaging.delivery.lag",
            unit: "s",
            description: "OccurredAt to consumer start.",
            tags: null,
            advice: LagBuckets);
        _projectionLag = meter.CreateHistogram<double>(
            "projection.lag",
            unit: "s",
            description: "Event raised to projection applied.",
            tags: null,
            advice: LagBuckets);
        _rejected = meter.CreateCounter<long>(
            "command.domain_rejected",
            description: "Message-borne commands the domain refused (§9.8).");
        _suppressed = meter.CreateCounter<long>(
            "messaging.inbox.suppressed",
            description: "Messages the inbox dropped as already handled (§9.5).");
    }

    public void Delivered(string message, TimeSpan lag) =>
        _deliveryLag.Record(lag.TotalSeconds, new KeyValuePair<string, object?>("message", message));

    public void Projected(string message, TimeSpan lag) =>
        _projectionLag.Record(lag.TotalSeconds, new KeyValuePair<string, object?>("message", message));

    /// <summary>A command the domain refused (§9.8), tagged by <c>Error.Code</c>, not a cancellation reason.</summary>
    public void Rejected(string message, string error) =>
        _rejected.Add(
            1,
            new KeyValuePair<string, object?>("message", message),
            new KeyValuePair<string, object?>("error", error));

    /// <summary>A message §9.5's inbox dropped as already recorded for this endpoint.</summary>
    /// <remarks>The <c>MessageId</c> is unbounded, so it goes on the filter's log line, never a tag (§13.3).</remarks>
    public void Suppressed(string message, string endpoint) =>
        _suppressed.Add(
            1,
            new KeyValuePair<string, object?>("message", message),
            new KeyValuePair<string, object?>("endpoint", endpoint));
}
