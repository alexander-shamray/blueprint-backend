using System.Diagnostics.Metrics;
using Notifications.Application.Push;
using Notifications.Infrastructure.Observability;

namespace Notifications.Infrastructure.Push;

/// <summary>Provider faults by cause and refusals by reason, so a provider's mass refusal is a series.</summary>
/// <remarks>Facts about a provider, not a notification, so §13.3's claim rule does not reach them.</remarks>
public sealed class PushMetrics
{
    private readonly Counter<long> _unavailable;
    private readonly Counter<long> _refused;

    public PushMetrics(IMeterFactory factory)
    {
        Meter meter = factory.Create(OutboundMeter.Name);
        _unavailable = meter.CreateCounter<long>(
            "notifications.push.unavailable",
            unit: "{attempt}",
            description: "Provider attempts that ended in a fault rather than an answer; the row backs off.");
        _refused = meter.CreateCounter<long>(
            "notifications.push.refused",
            unit: "{push}",
            description: "Pushes a provider refused for good; a sandbox token sent to production shows here.");
    }

    public void Unavailable(string provider, PushFault cause) =>
        _unavailable.Add(
            1,
            new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("cause", Cause(cause)));

    public void Refused(string provider, PushRefusal reason) =>
        _refused.Add(
            1,
            new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("reason", Reason(reason)));

    // Spelled out rather than ToString(), so the attribute's vocabulary is these switches and not an enum's casing.
    private static string Cause(PushFault cause) => cause switch
    {
        PushFault.Transient => "transient",
        PushFault.Unconfirmed => "unconfirmed",
        PushFault.Tls => "tls",
        PushFault.Credential => "credential",
        PushFault.Rejected => "rejected",
        _ => "unknown"
    };

    private static string Reason(PushRefusal reason) => reason switch
    {
        PushRefusal.Unregistered => "unregistered",
        PushRefusal.InvalidToken => "invalid_token",
        _ => "unknown"
    };
}
