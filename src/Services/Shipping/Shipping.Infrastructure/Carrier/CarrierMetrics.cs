using System.Diagnostics.Metrics;

namespace Shipping.Infrastructure.Carrier;

/// <summary>
/// One attempt that met a failing carrier: a fault rather than an answer.
/// §13.3's claim rule does not reach it — a pass that rolls back still met
/// a failing carrier.
/// </summary>
/// <remarks>
/// The meter is named for the work that leaves this service and not for the
/// carrier: the other two instruments it will carry are the address
/// adapter's and the waiting gauge's (§13.2).
/// </remarks>
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
