using Common.Application;
using Common.Web;
using Web.Bff.Orders;

namespace Web.Bff.Endpoints;

/// <summary>§10.7's buyer order read, served from ADR-051's projection and calling nothing.</summary>
public static class OrderEndpoints
{
    public static void MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app
            .MapGroup("/v1/orders")
            .WithTags("Orders")
            // Fail closed at the group (§11.4); the subject below is the principal's, never the request's (§10.7).
            .RequireAuthorization();

        // CursorPage, not Result (§6.2): a list has no failure to map.
        group
            .MapGet(
                "/",
                async (
                    string? cursor,
                    ICurrentUser user,
                    OrderReader orders,
                    CancellationToken ct,
                    int limit = OrderPage.DefaultLimit) =>
                    Results.Ok(await orders.ListAsync(user.Id, cursor, limit, ct)))
            .WithName("ListOrders");

        group
            .MapGet(
                "/{id:guid}",
                async (Guid id, ICurrentUser user, OrderReader orders, CancellationToken ct) =>
                    (await orders.FindAsync(user.Id, id, ct)).ToHttpResult())
            .WithName("GetOrder");
    }
}
