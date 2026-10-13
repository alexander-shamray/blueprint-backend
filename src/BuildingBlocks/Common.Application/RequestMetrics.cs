using System.Diagnostics.Metrics;

namespace Common.Application;

/// <summary>§13.3's <c>request.duration</c>, injected by <see cref="LoggingBehavior{TRequest,TResult}"/>.</summary>
public sealed class RequestMetrics
{
    /// <summary>Seconds-scale bounds, which the SDK defaults only for instruments it knows by name (§13.3).</summary>
    private static readonly InstrumentAdvice<double> DurationBuckets = new()
    {
        HistogramBucketBoundaries = [0.005, 0.01, 0.025, 0.05, 0.08, 0.1, 0.25, 0.5, 1, 2.5, 5, 10]
    };

    private readonly Histogram<double> _duration;

    public RequestMetrics(IMeterFactory factory)
    {
        Meter meter = factory.Create("Commerce.Requests");
        _duration = meter.CreateHistogram<double>(
            "request.duration",
            unit: "s",
            description: "Dispatcher entry to result.",
            tags: null,
            advice: DurationBuckets);
    }

    public void Recorded(string request, string outcome, TimeSpan elapsed) =>
        _duration.Record(
            elapsed.TotalSeconds,
            new KeyValuePair<string, object?>("request", request),
            new KeyValuePair<string, object?>("outcome", outcome));
}
