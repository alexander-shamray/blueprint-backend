using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Shipping.Infrastructure.Addresses;
using Shipping.Infrastructure.Carrier;
using Shouldly;

namespace Shipping.Worker.Tests;

/// <summary>One host's outbound counter, matched by meter instance as a <c>MeterListener</c> is process-wide.</summary>
internal static class OutboundCounter
{
    public static OutboundCount Unavailable(IServiceProvider services)
    {
        services.GetRequiredService<CarrierMetrics>();
        return Of(services, "shipping.carrier.unavailable");
    }

    public static OutboundCount Refused(IServiceProvider services)
    {
        services.GetRequiredService<AddressMetrics>();
        return Of(services, "shipping.address.refused");
    }

    private static OutboundCount Of(IServiceProvider services, string instrument)
    {
        Meter mine = services.GetRequiredService<IMeterFactory>().Create(CarrierMetrics.MeterName);
        OutboundCount count = new(mine, instrument);
        count.Enabled.ShouldBeTrue($"no {instrument} on this host's meter was enabled, so a zero would prove nothing");
        return count;
    }
}

internal sealed class OutboundCount : IDisposable
{
    private readonly MeterListener _listener = new();
    private long _counted;

    public OutboundCount(Meter mine, string instrument)
    {
        _listener.InstrumentPublished = (published, l) =>
        {
            if (ReferenceEquals(published.Meter, mine) && published.Name == instrument)
            {
                l.EnableMeasurementEvents(published);
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
