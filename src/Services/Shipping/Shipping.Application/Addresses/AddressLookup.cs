using Shipping.Application.Carrier;

namespace Shipping.Application.Addresses;

/// <summary>The owner's answer about one order.</summary>
public abstract record AddressLookup
{
    private AddressLookup()
    {
    }

    /// <summary>
    /// The address, and the customer it belongs to. The second member is
    /// carried for erasure's sake alone (ADR-052): it is stored beside the
    /// address and nowhere else, and the shipment's own record holds none.
    /// </summary>
    public sealed record Found(DeliveryAddress Address, Guid CustomerId) : AddressLookup;

    /// <summary>
    /// No such order, a cancelled one, or one whose address erasure has
    /// cleared. One answer for the three, because ADR-052 collapses them at
    /// the owner so that no reader can recover an order's state from a status.
    /// </summary>
    public sealed record NoSuchOrder : AddressLookup;
}
