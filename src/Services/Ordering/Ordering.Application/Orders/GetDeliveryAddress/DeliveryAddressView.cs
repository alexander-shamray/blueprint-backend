namespace Ordering.Application.Orders.GetDeliveryAddress;

/// <summary>
/// The five parts of <c>Address</c> plus the order's customer, which the
/// reader keeps for erasure's sake alone (ADR-052).
/// </summary>
public sealed record DeliveryAddressView(
    Guid CustomerId,
    string Line1,
    string? Line2,
    string City,
    string PostalCode,
    string Country);
