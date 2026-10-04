using System.Diagnostics.Metrics;

namespace Web.Bff.Observability;

/// <summary>The projection's own gauge, beside the delivery lag every consumer already records (§13.3).</summary>
public sealed class ProjectionMetrics
{
    /// <summary>Must equal the name §13.2's <c>AddMeter</c> registers, or nothing collects the gauge.</summary>
    public const string MeterName = "Web.Bff.Projection";

    // CA1848 (ADR-019), as ShipmentMetrics does.
    private static readonly Action<ILogger, Exception?> GaugeReadFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(1, nameof(GaugeReadFailed)),
            "Unattributed-order gauge read failed; this collection omits it rather than reporting a zero.");

    public ProjectionMetrics(IMeterFactory factory, IProjectionStats stats, ILogger<ProjectionMetrics> logger)
    {
        Meter meter = factory.Create(MeterName);

        // A row with no owner is invisible to its buyer (§10.7), and no delivery lag shows it.
        meter.CreateObservableGauge(
            "bff.orders.unattributed",
            () => Unattributed(stats, logger),
            unit: "s",
            description: "How long the oldest order with no owner has waited for one.");
    }

    /// <summary>Contained, since the collector abandons its pass on an exception (§13.6).</summary>
    private static List<Measurement<double>> Unattributed(IProjectionStats stats, ILogger logger)
    {
        try
        {
            return [new Measurement<double>(stats.UnattributedAgeSeconds())];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            GaugeReadFailed(logger, exception);
            return [];
        }
    }
}
