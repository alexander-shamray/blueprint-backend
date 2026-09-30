using Common.Application;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Ordering.Application.Orders.GetDeliveryAddress;
using Ordering.Delivery.V1;

namespace Ordering.Api.Grpc;

/// <summary>The server half of ADR-052's address read, under §4.2's gate like <c>OrderEndpoints</c>.</summary>
/// <remarks>A permission and no ownership check, since a service account owns no order (§11.4, ADR-052).</remarks>
[Authorize(OrderingPermissions.DeliveryAddress)]
internal sealed class DeliveryAddressService(IDispatcher dispatcher) : DeliveryAddresses.DeliveryAddressesBase
{
    public override async Task<GetDeliveryAddressReply> Get(
        GetDeliveryAddressRequest request,
        ServerCallContext context)
    {
        // "D" only, as the contract states; TryParse would also accept the N, B and P formats.
        if (!Guid.TryParseExact(request.OrderId, "D", out Guid orderId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "order_id is not a GUID."));

        DeliveryAddressView? address = await dispatcher.QueryAsync(
            new GetDeliveryAddressQuery(orderId),
            context.CancellationToken);

        // One status for three facts, and no detail that would recover the distinction (ADR-052).
        if (address is null)
            throw new RpcException(new Status(StatusCode.NotFound, "No delivery address for that order."));

        return new GetDeliveryAddressReply
        {
            CustomerId = address.CustomerId.ToString(),
            Line1 = address.Line1,
            // proto3 has no null string; the absence of a second line is "".
            Line2 = address.Line2 ?? string.Empty,
            City = address.City,
            PostCode = address.PostalCode,
            Country = address.Country
        };
    }
}
