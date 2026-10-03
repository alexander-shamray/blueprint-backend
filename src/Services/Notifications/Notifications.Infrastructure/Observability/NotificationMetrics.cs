using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace Notifications.Infrastructure.Observability;

/// <summary>The send worker's instruments, on <see cref="OutboundMeter.Name"/>, which §13.2 collects.</summary>
/// <remarks><c>ShipmentMetrics</c>' gauges for one pass: the waiting by step, and the overdue (§13.6).</remarks>
public sealed class NotificationMetrics
{
    // CA1848 (ADR-019), as ShipmentMetrics does.
    private static readonly Action<ILogger, Exception?> WaitingReadFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(1, nameof(WaitingReadFailed)),
            "Waiting-notification gauge read failed; this collection omits every step rather than reporting some.");

    private static readonly Action<ILogger, Exception?> OverdueReadFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(2, nameof(OverdueReadFailed)),
            "Overdue-notification gauge read failed; this collection omits it rather than reporting no wait.");

    private readonly Counter<long> _resent;

    public NotificationMetrics(IMeterFactory factory, INotificationStats stats, ILogger<NotificationMetrics> logger)
    {
        Meter meter = factory.Create(OutboundMeter.Name);

        // A count of possible duplicates: the row holds the intent, and this counts each send over one, a retry
        // after a transient fault included.
        _resent = meter.CreateCounter<long>(
            "notifications.mail.resent",
            unit: "{send}",
            description: "Sends started over an intent already stamped; a count of possible duplicates, an outage's retries included.");

        // Past a first backoff, by what the row waits on; a relay step rising during an outage is the breaker working.
        meter.CreateObservableGauge(
            "notifications.waiting",
            () => PerStep(stats, logger),
            unit: "{notification}",
            description: "Pending notifications past their first failed pass, by the step they wait on.");

        // The row no pass has reached, which the gauge above cannot see; one that climbs while the relay answers
        // is too few replicas.
        meter.CreateObservableGauge(
            "notifications.overdue",
            () => Overdue(stats, logger),
            unit: "s",
            description: "How long the longest-due notification has waited unclaimed past two ticks.");
    }

    public void Resent() => _resent.Add(1);

    /// <summary>Every step or none: one missing from a <c>max by (step)</c> reads as a healthy zero.</summary>
    /// <remarks>Contained, since the collector abandons its pass on an exception (§13.6).</remarks>
    private static List<Measurement<double>> PerStep(INotificationStats stats, ILogger logger)
    {
        IReadOnlyDictionary<string, int> waiting;

        try
        {
            waiting = stats.WaitingByStep();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            WaitingReadFailed(logger, exception);
            return [];
        }

        return
        [
            .. WaitingSteps.All.Select(step => new Measurement<double>(
                waiting.TryGetValue(step, out int count) ? count : 0,
                new KeyValuePair<string, object?>("step", step)))
        ];
    }

    private static List<Measurement<double>> Overdue(INotificationStats stats, ILogger logger)
    {
        try
        {
            return [new Measurement<double>(stats.OverdueSeconds())];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            OverdueReadFailed(logger, exception);
            return [];
        }
    }
}
