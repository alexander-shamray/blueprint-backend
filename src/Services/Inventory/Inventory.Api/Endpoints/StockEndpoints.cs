using Common.Application;
using Common.Web;
using Inventory.Application.Stock.GetStock;
using Inventory.Application.Stock.SetOnHand;

namespace Inventory.Api.Endpoints;

public static class StockEndpoints
{
    public static void MapStockEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app
            .MapGroup("/v1/inventory/stock")
            .WithTags("Stock")
            .RequireAuthorization(InventoryPermissions.Admin);

        group
            .MapPut(
                "/{productId:guid}",
                async (Guid productId, SetOnHandRequest request, IDispatcher dispatcher, CancellationToken ct) =>
                {
                    Result result = await dispatcher.SendAsync(new SetOnHandCommand(productId, request.OnHand), ct);

                    return result.ToHttpResult();
                })
            // An absolute count, so a repeat sets what the first set (ADR-058).
            .RetrySafe(RetrySafety.Convergent)
            .WithName("SetOnHand");

        group
            .MapGet(
                "/{productId:guid}",
                async (Guid productId, IDispatcher dispatcher, CancellationToken ct) =>
                {
                    Result<StockDto> result = await dispatcher.QueryAsync(new GetStockQuery(productId), ct);

                    return result.ToHttpResult();
                })
            .Produces<StockDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetStock");
    }
}

// int? for the reason SetOnHandCommand gives: `{}` must be a 400, not a reset.
public sealed record SetOnHandRequest(int? OnHand);
