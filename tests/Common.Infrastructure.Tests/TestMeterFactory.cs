using System.Diagnostics.Metrics;

namespace Common.Infrastructure.Tests;

/// <summary>The smallest <see cref="IMeterFactory"/>, rather than a package reference for <c>AddMetrics</c>.</summary>
internal sealed class TestMeterFactory : IMeterFactory
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
    }
}
