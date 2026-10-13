using Common.Infrastructure.Messaging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using Shouldly;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>§13.3's two lags as the SDK exports them, since its default bounds are milliseconds.</summary>
public class MessagingMetricsTests
{
    // §13.7's targets, restated so a bound deleted in production does not delete the expectation.
    private static readonly double[] Targets = [1, 2];

    [Theory]
    [InlineData("messaging.delivery.lag")]
    [InlineData("projection.lag")]
    public void A_healthy_lag_is_exported_in_a_bucket_a_tenth_of_a_second_wide(string instrument)
    {
        IReadOnlyList<HistogramBucket> buckets = Export(instrument, TimeSpan.FromMilliseconds(50));

        HistogramBucket landed = buckets.First(b => b.BucketCount > 0);

        landed.ExplicitBound.ShouldBeLessThanOrEqualTo(0.1);
    }

    [Theory]
    [InlineData("messaging.delivery.lag")]
    [InlineData("projection.lag")]
    public void Every_target_is_a_bound_and_a_lag_past_them_is_still_resolved(string instrument)
    {
        IReadOnlyList<HistogramBucket> buckets = Export(instrument, TimeSpan.FromSeconds(4));

        double[] bounds = [.. buckets.Select(b => b.ExplicitBound).Where(double.IsFinite)];

        Targets.ShouldBeSubsetOf(bounds);
        buckets.First(b => b.BucketCount > 0).ExplicitBound.ShouldBeLessThan(double.PositiveInfinity);
    }

    [Theory]
    [InlineData("messaging.delivery.lag")]
    [InlineData("projection.lag")]
    public void A_lag_just_under_the_first_target_is_exported_well_short_of_it(string instrument)
    {
        IReadOnlyList<HistogramBucket> buckets = Export(instrument, TimeSpan.FromMilliseconds(527));

        buckets.First(b => b.BucketCount > 0).ExplicitBound.ShouldBeLessThanOrEqualTo(0.6);
    }

    [Theory]
    [InlineData("messaging.delivery.lag")]
    [InlineData("projection.lag")]
    public void A_bound_sits_within_a_tenth_of_a_second_below_the_first_target(string instrument)
    {
        IReadOnlyList<HistogramBucket> buckets = Export(instrument, TimeSpan.FromMilliseconds(850));

        double landed = buckets.First(b => b.BucketCount > 0).ExplicitBound;

        landed.ShouldBeLessThan(Targets[0]);
        landed.ShouldBeGreaterThanOrEqualTo(Targets[0] - 0.1);
    }

    private static List<HistogramBucket> Export(string instrument, TimeSpan lag)
    {
        string message = $"Probe{Guid.NewGuid():N}";
        List<MetricSnapshot> exported = [];

        using MeterProvider provider = Sdk
            .CreateMeterProviderBuilder()
            .AddMeter("Commerce.Messaging")
            .AddInMemoryExporter(exported)
            .Build();
        using TestMeterFactory factory = new();

        MessagingMetrics metrics = new(factory);
        metrics.Delivered(message, lag);
        metrics.Projected(message, lag);

        provider.ForceFlush();

        MetricPoint point = exported
            .Where(m => m.Name == instrument)
            .SelectMany(m => m.MetricPoints)
            .Single(p => Carries(p, message));

        List<HistogramBucket> buckets = [];

        foreach (HistogramBucket bucket in point.GetHistogramBuckets())
            buckets.Add(bucket);

        return buckets;
    }

    private static bool Carries(MetricPoint point, string message)
    {
        foreach (KeyValuePair<string, object?> tag in point.Tags)
        {
            if (tag.Key == "message" && Equals(tag.Value, message))
                return true;
        }

        return false;
    }
}
