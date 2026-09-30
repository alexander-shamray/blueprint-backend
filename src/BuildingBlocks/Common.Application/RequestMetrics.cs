using System.Diagnostics.Metrics;

namespace Common.Application;

/// <summary>§13.3's <c>request.duration</c>, injected by <see cref="LoggingBehavior{TRequest,TResult}"/>.</summary>
public sealed class RequestMetrics
{
    private readonly Histogram<double> _duration;

    public RequestMetrics(IMeterFactory factory)
    {
        Meter meter = factory.Create("Commerce.Requests");
        _duration = meter.CreateHistogram<double>(
            "request.duration",
            unit: "s",
            description: "Dispatcher entry to result.");
    }

    public void Recorded(string request, string outcome, TimeSpan elapsed) =>
        _duration.Record(
            elapsed.TotalSeconds,
            new KeyValuePair<string, object?>("request", request),
            new KeyValuePair<string, object?>("outcome", outcome));
}
