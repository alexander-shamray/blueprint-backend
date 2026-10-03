using System.Diagnostics.Metrics;
using Notifications.Infrastructure.Observability;

namespace Notifications.Infrastructure.Contacts;

/// <summary>Contact reads refused over this host's credential or its grant, the defect ADR-052 counts.</summary>
/// <remarks>On <see cref="OutboundMeter.Name"/>, so §13.2's one <c>AddMeter</c> line covers it.</remarks>
public sealed class ContactMetrics
{
    private readonly Counter<long> _refused;

    public ContactMetrics(IMeterFactory factory)
    {
        Meter meter = factory.Create(OutboundMeter.Name);
        _refused = meter.CreateCounter<long>(
            "notifications.contact.refused",
            unit: "{refusal}",
            description: "Contact reads refused over this host's credential or its grant rather than failing.");
    }

    public void Refused() => _refused.Add(1);
}
