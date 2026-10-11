using Common.Contracts.Shipping.V1;
using static Platform.IntegrationTests.Contracts.SampleValues;

namespace Platform.IntegrationTests.Contracts.Shipping;

/// <summary>Shipping's samples, which publish events and accept no command.</summary>
internal sealed class ShippingContracts : ContractContext
{
    public override string Service => "Shipping";

    public override IReadOnlyDictionary<Type, Func<object>> Samples { get; } = new Dictionary<Type, Func<object>>
    {
        [typeof(ShipmentDispatched)] = () => new ShipmentDispatched
        {
            MessageId = Message,
            CorrelationId = Correlation,
            OccurredAt = Occurred,
            OrderId = Order,
            TrackingNumber = "TRK-99182"
        },
        [typeof(ShipmentDelivered)] = () => new ShipmentDelivered
        {
            MessageId = Message,
            CorrelationId = Correlation,
            OccurredAt = Occurred,
            OrderId = Order,
            TrackingNumber = "TRK-99182"
        }
    };
}
