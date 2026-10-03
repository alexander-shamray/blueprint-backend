using System.Diagnostics.Metrics;

namespace Notifications.Infrastructure.Observability;

/// <summary>The send worker's instruments, on <see cref="OutboundMeter.Name"/>, which §13.2 collects.</summary>
public sealed class NotificationMetrics
{
    private readonly Counter<long> _resent;

    public NotificationMetrics(IMeterFactory factory)
    {
        Meter meter = factory.Create(OutboundMeter.Name);

        // The record a possible duplicate leaves: the row holds the intent, and this counts each send over one.
        _resent = meter.CreateCounter<long>(
            "notifications.mail.resent",
            unit: "{send}",
            description: "Sends started over an intent already stamped, each one a message the relay may hold twice.");
    }

    public void Resent() => _resent.Add(1);
}
