using System.Diagnostics.Metrics;

namespace Ordering.Api.Tests;

/// <summary>The inbox filter's own count of the redeliveries it dropped, the signal that a replay ran.</summary>
internal static class InboxSuppression
{
    /// <summary>Releases <paramref name="suppressed"/> for each drop the inbox counts, as §9.5's filter does before it
    /// returns.</summary>
    public static MeterListener Listen(string message, string endpoint, SemaphoreSlim suppressed)
    {
        MeterListener listener = new();

        listener.InstrumentPublished = (instrument, active) =>
        {
            if (instrument.Meter.Name == "Commerce.Messaging" &&
                instrument.Name == "messaging.inbox.suppressed")
            {
                active.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            if (TagValue(tags, "message") == message && TagValue(tags, "endpoint") == endpoint)
                suppressed.Release();
        });

        listener.Start();
        return listener;
    }

    private static string TagValue(ReadOnlySpan<KeyValuePair<string, object?>> tags, string name)
    {
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            if (tag.Key == name)
                return tag.Value?.ToString() ?? string.Empty;
        }

        return string.Empty;
    }
}
