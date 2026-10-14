using System.Diagnostics.Metrics;
using Notifications.Application.Push;
using Notifications.Infrastructure.Observability;

namespace Notifications.Infrastructure.Push;

/// <summary>Provider faults by cause and refusals by reason, so a provider's mass refusal is a series.</summary>
/// <remarks>
/// Operational series, not business facts, so §13.3's committed-path rule does not reach them: each says that a
/// provider or a removal is happening at all, and an erasure retried after a rollback counting its rows twice
/// over-reads a rate nobody bills or reconciles. With the resends and the worker's three writes to a device row
/// (ADR-118): two deletes and the erasure's clear.
/// </remarks>
public sealed class PushMetrics
{
    /// <summary>A device row the stale pass deleted (ADR-117).</summary>
    public const string Stale = "stale";

    /// <summary>A device row deleted on a provider's unregistered answer, under ADR-117's guard.</summary>
    public const string Unregistered = "unregistered";

    /// <summary>A device row an erasure cleared, keeping its id until the stale pass deletes it (ADR-117).</summary>
    public const string Erased = "erased";

    private readonly Counter<long> _unavailable;
    private readonly Counter<long> _refused;
    private readonly Counter<long> _resent;
    private readonly Counter<long> _removed;

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

        // A count of possible duplicates on a device, which the event's collapse key replaces rather than stacks.
        _resent = meter.CreateCounter<long>(
            "notifications.push.resent",
            unit: "{push}",
            description: "Pushes started over an intent already stamped; a count of possible duplicates.");
        _removed = meter.CreateCounter<long>(
            "notifications.devices.removed",
            unit: "{device}",
            description: "Device rows the worker deleted, stale or unregistered by a provider, or cleared, erased.");
    }

    public void Resent() => _resent.Add(1);

    /// <summary>Rows removed, for <see cref="Stale"/>, <see cref="Unregistered"/> or <see cref="Erased"/>.</summary>
    public void DevicesRemoved(string reason, int rows)
    {
        if (rows > 0)
            _removed.Add(rows, new KeyValuePair<string, object?>("reason", reason));
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
