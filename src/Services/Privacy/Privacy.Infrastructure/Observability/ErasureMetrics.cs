using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace Privacy.Infrastructure.Observability;

/// <summary>The gauges that make a holder's silence visible: requests overdue, and who has not answered (ADR-092).</summary>
/// <remarks>On <see cref="OutboxMetrics.MeterName"/>, so §13.2's one <c>AddMeter</c> line covers them.</remarks>
public sealed class ErasureMetrics
{
    // CA1848 (ADR-019), as OutboxMetrics does.
    private static readonly Action<ILogger, Exception?> GaugeReadFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(1, nameof(GaugeReadFailed)),
            "Erasure gauge read failed. This collection omits the gauge rather than reporting none, see ErasureMetrics.");

    public ErasureMetrics(IMeterFactory factory, IErasureStats stats, ILogger<ErasureMetrics> logger)
    {
        Meter meter = factory.Create(OutboxMetrics.MeterName);

        // Always one measurement, zero included, so that none overdue reads as a value and not as no data.
        meter.CreateObservableGauge(
            "privacy.erasure.overdue",
            () => Total(stats, logger),
            unit: "{request}",
            description: "Erasure requests past their due time with a holder still to answer.");

        // Only holders missing from something, so an absent series is none missing; the name is a closed set.
        meter.CreateObservableGauge(
            "privacy.erasure.overdue.missing",
            () => PerResponder(stats, logger),
            unit: "{request}",
            description: "Overdue erasure requests each holder has not answered, by holder.");
    }

    /// <summary>Contained, since the collector abandons its pass on an exception (§13.6).</summary>
    private static List<Measurement<int>> Total(IErasureStats stats, ILogger logger)
    {
        try
        {
            return [new(stats.OverdueCount())];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            GaugeReadFailed(logger, exception);
            return [];
        }
    }

    private static List<Measurement<int>> PerResponder(IErasureStats stats, ILogger logger)
    {
        try
        {
            return
            [
                .. stats.OverdueMissingByResponder()
                    .Select(pair => new Measurement<int>(pair.Value, new KeyValuePair<string, object?>("responder", pair.Key)))
            ];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            GaugeReadFailed(logger, exception);
            return [];
        }
    }
}
