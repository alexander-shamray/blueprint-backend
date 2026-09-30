using System.Diagnostics.Metrics;

namespace Shipping.Infrastructure.Carrier;

/// <summary>Attempts that met a failing carrier, whether the pipeline or the adapter saw the fault.</summary>
/// <remarks>A fact about the carrier, not a shipment, so §13.3's claim rule does not reach it.</remarks>
public sealed class CarrierMetrics
{
    public const string MeterName = "Shipping.Outbound";

    private readonly Counter<long> _unavailable;

    public CarrierMetrics(IMeterFactory factory)
    {
        Meter meter = factory.Create(MeterName);
        _unavailable = meter.CreateCounter<long>(
            "shipping.carrier.unavailable",
            unit: "{attempt}",
            description: "Carrier attempts that ended in a fault rather than an answer; the row backs off.");
    }

    public void Unavailable() => _unavailable.Add(1);
}
