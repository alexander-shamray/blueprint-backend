using System.Diagnostics.Metrics;
using Shipping.Infrastructure.Carrier;

namespace Shipping.Infrastructure.Addresses;

/// <summary>Refused credentials on the way to an address, the defect ADR-052 counts.</summary>
/// <remarks>On <see cref="CarrierMetrics.MeterName"/>, so §13.2's one <c>AddMeter</c> line covers it.</remarks>
public sealed class AddressMetrics
{
    private readonly Counter<long> _refused;

    public AddressMetrics(IMeterFactory factory)
    {
        Meter meter = factory.Create(CarrierMetrics.MeterName);
        _refused = meter.CreateCounter<long>(
            "shipping.address.refused",
            unit: "{refusal}",
            description: "Address reads refused over this host's credential or its grant rather than failing.");
    }

    public void Refused() => _refused.Add(1);
}
