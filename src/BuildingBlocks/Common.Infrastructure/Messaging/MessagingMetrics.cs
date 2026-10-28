using System.Diagnostics.Metrics;

namespace Common.Infrastructure.Messaging;

/// <summary>§13.3's messaging instruments, on the <c>Commerce.Messaging</c> meter.</summary>
/// <remarks>Both lags compare a timestamp made on another machine, so §13.7 targets them in seconds.</remarks>
public sealed class MessagingMetrics
{
    /// <summary>Seconds-scale bounds, which the SDK defaults only for instruments it knows by name (§13.3).</summary>
    private static readonly InstrumentAdvice<double> LagBuckets = new()
    {
        HistogramBucketBoundaries = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 0.6, 0.75, 0.9, 1, 2, 5, 10, 30]
    };

    /// <summary>Millisecond-scale bounds, since a row's delivery is one publish or one projection (§13.3).</summary>
    private static readonly InstrumentAdvice<double> OutboxDeliveryBuckets = new()
    {
        HistogramBucketBoundaries = [0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10]
    };

    private readonly Histogram<double> _deliveryLag;
    private readonly Histogram<double> _projectionLag;
    private readonly Counter<long> _rejected;
    private readonly Counter<long> _suppressed;
    private readonly Histogram<double> _outboxDelivery;

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
        _outboxDelivery = meter.CreateHistogram<double>(
            "outbox.delivery.duration",
            unit: "s",
            description: "One outbox row's delivery, by lane and outcome (§9.4).",
            tags: null,
            advice: OutboxDeliveryBuckets);
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

    /// <summary>One row the dispatcher delivered or failed; the delivered count is the lane's drain rate, which the
    /// pending count's net change cannot show while new rows arrive (§9.4).</summary>
    public void OutboxDelivered(string lane, string outcome, TimeSpan elapsed) =>
        _outboxDelivery.Record(
            elapsed.TotalSeconds,
            new KeyValuePair<string, object?>("lane", lane),
            new KeyValuePair<string, object?>("outcome", outcome));
}
