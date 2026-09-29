using System.Diagnostics.Metrics;

namespace Shipping.Infrastructure.Carrier;

/// <summary>
/// One attempt that met a failing carrier: a fault rather than an answer.
/// §13.3's claim rule does not reach it — a pass that rolls back still met
/// a failing carrier.
/// </summary>
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
