using Common.Application;
using Common.Web;
using Ordering.Application.Orders;
using Ordering.Application.Orders.CancelOrder;
using Ordering.Application.Orders.PlaceOrder;
using Ordering.Domain.Orders;

namespace Ordering.Api.Endpoints;

/// <summary>One static class per aggregate (ADR-015), at <c>/v1/orders</c>: the gateway strips <c>/api</c>.</summary>
/// <remarks>Nothing here is anonymous: §10.2's <c>ordering</c> route requires authentication.</remarks>
public static class OrderEndpoints
{
    public static void MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app
            .MapGroup("/v1/orders")
            .WithTags("Orders")
            // Fail closed at the group (§11.4): a later endpoint inherits authentication rather than arriving open.
            .RequireAuthorization();

        // Bound straight from the body: the command carries no CustomerId to bind (§11.4).
        group
            .MapPost(
                "/",
                async (PlaceOrderCommand command, IDispatcher dispatcher, CancellationToken ct) =>
                {
                    Result<Guid> result = await dispatcher.SendAsync(command, ct);

                    return result.ToHttpResult();
                })
            .RequireAuthorization(OrderingPermissions.Write)
            .WithRequestExample(
                new PlaceOrderCommand(
                    Guid.Parse("0199b0c4-8b21-7c53-8e7f-4a6b8c9d0e1f"),
                    [new PlaceOrderItem(Guid.Parse("0199b0c4-6f2e-7a31-8c5d-2e4f6a7b8c9d"), 2)],
                    new AddressDto("1 Example Street", null, "Sampleton", "1234 AB", "NL"),
                    "EUR"))
            .WithName("PlaceOrder");

        // A request record, since the reason is parsed here and the origin is not the caller's to state (§11.4).
        group
            .MapPost(
                "/{id:guid}/cancel",
                async (
                    Guid id,
                    CancelOrderRequest request,
                    IDispatcher dispatcher,
                    CancellationToken ct) =>
                {
                    if (!CancellationReasons.TryParse(request.Reason, out CancellationReason reason))
                    {
                        return Results.ValidationProblem(
                            new Dictionary<string, string[]>
                            {
                                [nameof(request.Reason)] = ["Not a known cancellation reason."]
                            });
                    }

                    Result result = await dispatcher.SendAsync(
                        new CancelOrderCommand(id, reason, CommandOrigin.User),
                        ct);

                    return result.ToHttpResult();
                })
            .RequireAuthorization(OrderingPermissions.Cancel)
            // Order.Cancel returns on a cancelled order, and cancelled is terminal (§5.4).
            .RetrySafe(RetrySafety.Convergent)
            .WithRequestExample(new CancelOrderRequest(CancellationReasons.ToCode(CancellationReason.CustomerRequest)))
            .WithName("CancelOrder");
    }
}

/// <summary>A string reason, so an unknown one is a 400 naming the field rather than a binding failure.</summary>
public sealed record CancelOrderRequest(string Reason);
