using Common.Application;
using Common.Web;
using Inventory.Application;
using Inventory.Application.Reservations.GetReservation;
using Inventory.Application.Reservations.Reinstate;
using Inventory.Application.Reservations.ReleaseStock;

namespace Inventory.Api.Endpoints;

public static class ReservationEndpoints
{
    public static void MapReservationEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app
            .MapGroup("/v1/inventory/reservations")
            .WithTags("Reservations")
            .RequireAuthorization(InventoryPermissions.Admin);

        group
            .MapGet(
                "/{orderId:guid}",
                async (Guid orderId, IDispatcher dispatcher, CancellationToken ct) =>
                {
                    ReservationDto? dto = await dispatcher.QueryAsync(new GetReservationQuery(orderId), ct);

                    return dto is null ? Results.NotFound() : Results.Ok(dto);
                })
            .WithName("GetReservation");

        group
            .MapPost(
                "/{orderId:guid}/release",
                async (Guid orderId, IDispatcher dispatcher, CancellationToken ct) =>
                {
                    Result result = await dispatcher.SendAsync(
                        new ReleaseStockCommand(orderId, CommandOrigin.User), ct);

                    return result.ToHttpResult();
                })
            .WithName("ReleaseReservation");

        group
            .MapPost(
                "/{orderId:guid}/reinstate",
                async (Guid orderId, IDispatcher dispatcher, CancellationToken ct) =>
                {
                    Result result = await dispatcher.SendAsync(new ReinstateReservationCommand(orderId), ct);

                    return result.ToHttpResult();
                })
            .WithName("ReinstateReservation");
    }
}
