namespace Shipping.Application.Carrier;

/// <summary>All the carrier is shown besides the shipment's id; <c>Order.ShippingAddress</c> holds no name.</summary>
public sealed record DeliveryAddress(string Line1, string? Line2, string City, string PostalCode, string Country);
