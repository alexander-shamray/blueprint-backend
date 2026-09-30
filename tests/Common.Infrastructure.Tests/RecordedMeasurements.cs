using System.Diagnostics.Metrics;

namespace Common.Infrastructure.Tests;

/// <summary>One meter's measurements and tags, read through a <see cref="MeterListener"/>.</summary>
internal sealed class RecordedMeasurements : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly List<Measurement> _taken = [];
    private readonly Lock _gate = new();

    public RecordedMeasurements(string meterName)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == meterName)
                listener.EnableMeasurementEvents(instrument);
        };

        // Both shapes, since the meter carries double histograms and a long counter.
        _listener.SetMeasurementEventCallback<double>(
            (instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.SetMeasurementEventCallback<long>(
            (instrument, value, tags, _) => Record(instrument, value, tags));

        _listener.Start();
    }

    internal sealed record Measurement(string Instrument, double Value, KeyValuePair<string, object?>[] Tags)
    {
        public string? Tag(string name) =>
            Tags.FirstOrDefault(t => t.Key == name).Value?.ToString();
    }

    /// <summary>Every measurement one instrument took, in order.</summary>
    public IReadOnlyList<Measurement> For(string instrument)
    {
        lock (_gate)
            return [.. _taken.Where(m => m.Instrument == instrument)];
    }

    public void Dispose() => _listener.Dispose();

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        // The span cannot escape the callback, so it is copied before the lock.
        KeyValuePair<string, object?>[] copied = [.. tags];

        lock (_gate)
            _taken.Add(new Measurement(instrument.Name, value, copied));
    }
}
