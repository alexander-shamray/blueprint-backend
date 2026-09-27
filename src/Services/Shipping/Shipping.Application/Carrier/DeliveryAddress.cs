namespace Shipping.Application.Carrier;

/// <summary>
/// The address as the carrier is shown it, and the whole of what it is shown
/// besides the shipment's id (spec, section 9). It carries no recipient's
/// name, because <c>Order.ShippingAddress</c> holds none.
/// </summary>
public sealed record DeliveryAddress(string Line1, string? Line2, string City, string PostalCode, string Country);
