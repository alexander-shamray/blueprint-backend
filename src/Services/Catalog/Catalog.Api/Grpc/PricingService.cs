using System.Globalization;
using Catalog.Application.Products.GetPrices;
using Catalog.Pricing.V1;
using Common.Application;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using PricingGrpc = Catalog.Pricing.V1.Pricing;

namespace Catalog.Api.Grpc;

/// <summary>The server half of §9.7's one permitted synchronous hop, a transport adapter under §4.2's gate.</summary>
/// <remarks>
/// Authenticated, so the BFF's client credentials (§11.5) are checked; no permission, because its service account
/// carries none (§11.4). The alias exists because the namespace <c>Catalog.Pricing</c> shadows the bare name.
/// </remarks>
[Authorize]
internal sealed class PricingService(IDispatcher dispatcher) : PricingGrpc.PricingBase
{
    public override async Task<GetPricesReply> GetPrices(GetPricesRequest request, ServerCallContext context)
    {
        // Parsed here, as a wire concern, so a malformed id is InvalidArgument rather than a validation failure.
        Guid[] productIds = new Guid[request.ProductId.Count];

        for (int i = 0; i < request.ProductId.Count; i++)
        {
            // "D" only: the contract says canonical text form, and TryParse also accepts N, B and P.
            if (!Guid.TryParseExact(request.ProductId[i], "D", out productIds[i]))
            {
                // The index, never the value: §13.4's redactor cannot see a value interpolated into a message.
                throw new RpcException(new Status(
                    StatusCode.InvalidArgument,
                    $"product_id[{i}] is not a GUID."));
            }
        }

        IReadOnlyList<ProductPriceDto> prices = await dispatcher.QueryAsync(
            new GetPricesQuery(productIds, request.Currency),
            context.CancellationToken);

        GetPricesReply reply = new();

        foreach (ProductPriceDto price in prices)
        {
            reply.Price.Add(new ProductPrice
            {
                ProductId = price.ProductId.ToString(),
                Name = price.Name,
                // InvariantCulture on both sides of the wire, or a German locale's "12,50" parses as 1250.
                Amount = price.Amount.ToString(CultureInfo.InvariantCulture),
                Currency = price.Currency
            });
        }

        return reply;
    }
}
