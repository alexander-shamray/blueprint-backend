using Shipping.Application.Carrier;

namespace Shipping.Application.Addresses;

/// <summary>The owner's answer about one order.</summary>
public abstract record AddressLookup
{
    private AddressLookup()
    {
    }

    /// <summary>The address, and its customer, carried for erasure alone (ADR-052).</summary>
    public sealed record Found(DeliveryAddress Address, Guid CustomerId) : AddressLookup;

    /// <summary>No such order, a cancelled one, or an erased address, collapsed at the owner (ADR-052).</summary>
    public sealed record NoSuchOrder : AddressLookup;
}
