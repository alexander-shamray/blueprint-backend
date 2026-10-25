using Common.Application;
using Grpc.Core;
using Inventory.Application.Stock.GetAvailability;
using Inventory.Stock.V1;
using Microsoft.AspNetCore.Authorization;
using StockGrpc = Inventory.Stock.V1.Stock;

namespace Inventory.Api.Grpc;

/// <summary>The server half of ADR-154's availability hop, a transport adapter under §4.2's gate.</summary>
/// <remarks>
/// Authenticated, so the BFF's client credentials (§11.5) are checked; no permission, because its service account
/// carries none (§11.4) and the reply holds no count. The alias exists because the namespace shadows the bare name.
/// </remarks>
[Authorize]
internal sealed class StockService(IDispatcher dispatcher) : StockGrpc.StockBase
{
    public override async Task<GetAvailabilityReply> GetAvailability(
        GetAvailabilityRequest request,
        ServerCallContext context)
    {
        Guid[] productIds = new Guid[request.ProductId.Count];

        for (int i = 0; i < request.ProductId.Count; i++)
        {
            // "D" only: the contract says canonical text form, and TryParse also accepts N, B and P.
            if (!Guid.TryParseExact(request.ProductId[i], "D", out productIds[i]))
            {
                // The index, never the value: §13.4's redactor cannot see a value interpolated into a message.
                throw new RpcException(
                    new Status(StatusCode.InvalidArgument, $"product_id[{i}] is not a GUID."));
            }
        }

        IReadOnlyList<ProductAvailabilityDto> rows = await dispatcher.QueryAsync(
            new GetAvailabilityQuery(productIds),
            context.CancellationToken);

        GetAvailabilityReply reply = new();

        foreach (ProductAvailabilityDto row in rows)
        {
            reply.Availability.Add(
                new ProductAvailability { ProductId = row.ProductId.ToString(), InStock = row.InStock });
        }

        return reply;
    }
}
