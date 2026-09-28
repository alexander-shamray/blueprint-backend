using System.Diagnostics.Metrics;
using Shipping.Infrastructure.Carrier;

namespace Shipping.Infrastructure.Addresses;

/// <summary>
/// One refused credential on the way to an address, whether the identity
/// provider refused this host or Ordering refused its token, on the outbound
/// meter <see cref="CarrierMetrics.MeterName"/> names (spec, section 11).
/// </summary>
/// <remarks>
/// <c>CarrierMetrics.MeterName</c> rather than a string of its own, so §13.2's
/// one <c>AddMeter</c> line covers both. <c>IMeterFactory</c> caches by name,
/// so the two classes hold one meter between them.
/// </remarks>
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
