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
            // A released reservation gives nothing back, so a repeat moves no stock (ADR-024).
            .RetrySafe(RetrySafety.Convergent)
            .WithName("ReleaseReservation");

        // A request record, since the order is the route's and the body carries the command id alone (§8.5).
        group
            .MapPost(
                "/{orderId:guid}/reinstate",
                async (
                    Guid orderId,
                    ReinstateReservationRequest request,
                    IDispatcher dispatcher,
                    CancellationToken ct) =>
                {
                    Result result = await dispatcher.SendAsync(
                        new ReinstateReservationCommand(request.CommandId, orderId), ct);

                    return result.ToHttpResult();
                })
            // Built from the route and the body, so no parameter names the command (§8.5).
            .Idempotent<ReinstateReservationCommand>()
            .WithName("ReinstateReservation");
    }
}

/// <summary>The caller's id for this reinstatement, in the body because §8.5 keeps it a field of the command.</summary>
public sealed record ReinstateReservationRequest(Guid CommandId);
