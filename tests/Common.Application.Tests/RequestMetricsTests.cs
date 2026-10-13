using System.Diagnostics.Metrics;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using Shouldly;
using Xunit;

namespace Common.Application.Tests;

/// <summary>§13.3's <c>request.duration</c> as the SDK exports it, since its default bounds are milliseconds.</summary>
public class RequestMetricsTests
{
    // §13.7's targets, restated so a bound deleted in production does not delete the expectation.
    private static readonly double[] Targets = [0.08, 0.1];
    [Fact]
    public void A_recorded_request_carries_its_name_and_its_outcome()
    {
        using TestMeterFactory factory = new();
        using MeasurementCollector collector = MeasurementCollector.ForRequests();
        RequestMetrics metrics = new(factory);

        metrics.Recorded("PlaceOrderCommand", "ok", TimeSpan.FromMilliseconds(1500));

        RecordedMeasurement measurement = collector.For("PlaceOrderCommand");

        measurement.Instrument.ShouldBe("request.duration");
        measurement.Tags["outcome"].ShouldBe("ok");
    }

    [Fact]
    public void A_duration_is_recorded_in_seconds()
    {
        // Seconds, the unit §13.7's p95 targets are read in.
        using TestMeterFactory factory = new();
        using MeasurementCollector collector = MeasurementCollector.ForRequests();
        RequestMetrics metrics = new(factory);

        metrics.Recorded("GetOrderSummariesQuery", "ok", TimeSpan.FromMilliseconds(1500));

        collector.For("GetOrderSummariesQuery").Value.ShouldBe(1.5);
    }

    [Fact]
    public void A_healthy_request_is_exported_in_a_bucket_a_tenth_of_a_second_wide()
    {
        IReadOnlyList<HistogramBucket> buckets = Export(TimeSpan.FromMilliseconds(40));

        buckets.First(b => b.BucketCount > 0).ExplicitBound.ShouldBeLessThanOrEqualTo(0.1);
    }

    [Fact]
    public void Every_target_is_a_bound_and_a_slow_request_is_still_resolved()
    {
        IReadOnlyList<HistogramBucket> buckets = Export(TimeSpan.FromSeconds(4));

        double[] bounds = [.. buckets.Select(b => b.ExplicitBound).Where(double.IsFinite)];

        Targets.ShouldBeSubsetOf(bounds);
        buckets.First(b => b.BucketCount > 0).ExplicitBound.ShouldBeLessThan(double.PositiveInfinity);
    }

    [Fact]
    public void Every_histogram_on_the_meter_gives_bucket_advice()
    {
        // A histogram added later without advice falls back to the SDK's millisecond bounds.
        List<Instrument> published = [];
        using TestMeterFactory factory = new();
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, _) => published.Add(instrument);
        listener.Start();

        _ = new RequestMetrics(factory);

        List<Histogram<double>> histograms = [.. published.OfType<Histogram<double>>()];

        histograms.ShouldNotBeEmpty();
        histograms.ShouldAllBe(h => h.Advice != null && h.Advice.HistogramBucketBoundaries != null);
    }

    private static List<HistogramBucket> Export(TimeSpan elapsed)
    {
        string request = $"Probe{Guid.NewGuid():N}";
        List<MetricSnapshot> exported = [];

        using MeterProvider provider = Sdk
            .CreateMeterProviderBuilder()
            .AddMeter("Commerce.Requests")
            .AddInMemoryExporter(exported)
            .Build();
        using TestMeterFactory factory = new();

        new RequestMetrics(factory).Recorded(request, "ok", elapsed);

        provider.ForceFlush();

        MetricPoint point = exported
            .Where(m => m.Name == "request.duration")
            .SelectMany(m => m.MetricPoints)
            .Single(p => Carries(p, request));

        List<HistogramBucket> buckets = [];

        foreach (HistogramBucket bucket in point.GetHistogramBuckets())
            buckets.Add(bucket);

        return buckets;
    }

    private static bool Carries(MetricPoint point, string request)
    {
        foreach (KeyValuePair<string, object?> tag in point.Tags)
        {
            if (tag.Key == "request" && Equals(tag.Value, request))
                return true;
        }

        return false;
    }
}
