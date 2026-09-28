using Common.Application;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Ordering.Application.Orders.GetDeliveryAddress;
using Ordering.Delivery.V1;

namespace Ordering.Api.Grpc;

/// <summary>
/// The server half of ADR-052's address read: parse, dispatch, project onto
/// the reply — the job <c>OrderEndpoints</c> does for HTTP, under §4.2's gate.
/// </summary>
/// <remarks>
/// A permission where Catalog's gRPC service asks only for authentication,
/// because this answers with somebody's address and an authenticated caller
/// alone is every client the realm holds. No ownership check either: a
/// service account's subject owns no order, so the grant bounds it (§11.4).
/// </remarks>
[Authorize(OrderingPermissions.DeliveryAddress)]
internal sealed class DeliveryAddressService(IDispatcher dispatcher) : DeliveryAddresses.DeliveryAddressesBase
{
    public override async Task<GetDeliveryAddressReply> Get(
        GetDeliveryAddressRequest request,
        ServerCallContext context)
    {
        // TryParseExact with "D", not TryParse: the contract says a GUID in its
        // canonical text form, and TryParse also accepts the N, B and P
        // formats. Accepting more than the contract states is how two ends stop
        // agreeing about what the contract is.
        if (!Guid.TryParseExact(request.OrderId, "D", out Guid orderId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "order_id is not a GUID."));

        DeliveryAddressView? address = await dispatcher.QueryAsync(
            new GetDeliveryAddressQuery(orderId),
            context.CancellationToken);

        // One status for three facts (ADR-052): no such order, a cancelled
        // one, and one whose address erasure has cleared. The detail says no
        // more than the status, so a caller cannot recover the distinction the
        // handler deliberately collapsed.
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
