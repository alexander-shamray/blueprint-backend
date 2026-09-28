using Common.Application;

namespace Ordering.Application.Orders.GetDeliveryAddress;

public sealed record GetDeliveryAddressQuery(Guid OrderId) : IQuery<DeliveryAddressView?>;
