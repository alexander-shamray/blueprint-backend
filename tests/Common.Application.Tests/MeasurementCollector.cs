using System.Diagnostics.Metrics;

namespace Common.Application.Tests;

/// <summary>One recorded measurement, with the tags flattened for assertion.</summary>
public sealed record RecordedMeasurement(
    string Instrument,
    double Value,
    IReadOnlyDictionary<string, object?> Tags);

/// <summary>Owns the meters it hands out, since the real factory's <c>AddMetrics</c> is host-side of §4.2.</summary>
public sealed class TestMeterFactory : IMeterFactory
{
    private readonly List<Meter> _meters = [];

    public Meter Create(MeterOptions options)
    {
        Meter meter = new(options);
        _meters.Add(meter);
        return meter;
    }

    public void Dispose()
    {
        foreach (Meter meter in _meters)
            meter.Dispose();

        _meters.Clear();
    }
}

/// <summary>Everything recorded on one meter, read through a <see cref="MeterListener"/>.</summary>
public sealed class MeasurementCollector : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly List<RecordedMeasurement> _measurements = [];

    public MeasurementCollector(string meterName)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == meterName)
                listener.EnableMeasurementEvents(instrument);
        };

        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            Dictionary<string, object?> flattened = [];

            foreach (KeyValuePair<string, object?> tag in tags)
                flattened[tag.Key] = tag.Value;

            lock (_measurements)
                _measurements.Add(new RecordedMeasurement(instrument.Name, value, flattened));
        });

        _listener.Start();
    }

    /// <summary>§13.3's meter name, spelled out rather than read from the class under test.</summary>
    public static MeasurementCollector ForRequests() => new("Commerce.Requests");

    public IReadOnlyList<RecordedMeasurement> Measurements
    {
        get
        {
            lock (_measurements)
                return [.. _measurements];
        }
    }

    /// <summary>The one measurement tagged with this request, as other test classes record here too.</summary>
    public RecordedMeasurement For(string request) =>
        Measurements.Single(m => m.Tags.TryGetValue("request", out object? value) && Equals(value, request));

    public void Dispose() => _listener.Dispose();
}
