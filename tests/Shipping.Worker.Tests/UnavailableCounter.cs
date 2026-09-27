using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Shipping.Infrastructure.Carrier;
using Shouldly;

namespace Shipping.Worker.Tests;

/// <summary>
/// One host's <c>shipping.carrier.unavailable</c> counter, never one matched
/// by name: a <c>MeterListener</c> is process-wide, so another host's carrier
/// would count into it. The provider is the caller's because the two suites
/// keep their hosts differently.
/// </summary>
internal static class UnavailableCounter
{
    public static UnavailableCount Of(IServiceProvider services)
    {
        services.GetRequiredService<CarrierMetrics>();
        Meter mine = services.GetRequiredService<IMeterFactory>().Create(CarrierMetrics.MeterName);
        UnavailableCount count = new(mine);
        count.Enabled.ShouldBeTrue("no counter on this host's meter was enabled, so a zero would prove nothing");
        return count;
    }
}

internal sealed class UnavailableCount : IDisposable
{
    private readonly MeterListener _listener = new();
    private long _counted;

    public UnavailableCount(Meter mine)
    {
        _listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, mine) && instrument.Name == "shipping.carrier.unavailable")
            {
                l.EnableMeasurementEvents(instrument);
                Enabled = true;
            }
        };
        _listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref _counted, value));
        _listener.Start();
    }

    public bool Enabled { get; private set; }

    public long Value => Interlocked.Read(ref _counted);

    public void Dispose() => _listener.Dispose();
}
