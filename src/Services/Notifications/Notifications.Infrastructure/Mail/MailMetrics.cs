using System.Diagnostics.Metrics;
using Notifications.Application.Mail;
using Notifications.Infrastructure.Observability;

namespace Notifications.Infrastructure.Mail;

/// <summary>Attempts that met a failing relay, by cause, so a refusal that is a decision reads apart.</summary>
/// <remarks>A fact about the relay, not a notification, so §13.3's claim rule does not reach it.</remarks>
public sealed class MailMetrics
{
    private readonly Counter<long> _unavailable;

    public MailMetrics(IMeterFactory factory)
    {
        Meter meter = factory.Create(OutboundMeter.Name);
        _unavailable = meter.CreateCounter<long>(
            "notifications.mail.unavailable",
            unit: "{attempt}",
            description: "Relay attempts that ended in a fault rather than an answer; the row backs off.");
    }

    public void Unavailable(MailFault cause) =>
        _unavailable.Add(1, new KeyValuePair<string, object?>("cause", Cause(cause)));

    // Spelled out rather than ToString(), so the attribute's vocabulary is this switch and not an enum's casing.
    private static string Cause(MailFault cause) => cause switch
    {
        MailFault.Transient => "transient",
        MailFault.Unconfirmed => "unconfirmed",
        MailFault.Tls => "tls",
        MailFault.Credential => "credential",
        MailFault.Rejected => "rejected",
        _ => "unknown"
    };
}
