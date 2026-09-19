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
            .WithName("SetOnHand");

        group
            .MapGet(
                "/{productId:guid}",
                async (Guid productId, IDispatcher dispatcher, CancellationToken ct) =>
                {
                    StockDto? stock = await dispatcher.QueryAsync(new GetStockQuery(productId), ct);

                    return stock is null ? Results.NotFound() : Results.Ok(stock);
                })
            .WithName("GetStock");
    }
}

// int? for the reason SetOnHandCommand gives: `{}` must be a 400, not a reset.
public sealed record SetOnHandRequest(int? OnHand);
