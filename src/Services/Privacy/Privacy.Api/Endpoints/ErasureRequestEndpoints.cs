using Common.Application;
using Common.Web;
using Privacy.Application.ErasureRequests.GetErasureRequest;
using Privacy.Application.ErasureRequests.RaiseErasureRequest;

namespace Privacy.Api.Endpoints;

/// <summary>
/// One static class per aggregate (ADR-015); the gateway strips <c>/api</c> (§10.2), so routes start at the version.
/// </summary>
/// <remarks>No gateway route: how an operator reaches an internal host is the deployment's (ADR-092).</remarks>
public static class ErasureRequestEndpoints
{
    public static void MapErasureRequestEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app
            .MapGroup("/v1/privacy/erasure-requests")
            .WithTags("Erasure requests")
            // Fail closed at the group (§11.4), so a new endpoint arrives unreachable rather than public.
            .RequireAuthorization();

        // Bound straight from the body, since the wire shape and the command are the same primitive.
        group
            .MapPost(
                "/",
                async (RaiseErasureRequestCommand command, IDispatcher dispatcher, CancellationToken ct) =>
                {
                    Result<Guid> result = await dispatcher.SendAsync(command, ct);

                    return result.ToHttpResult();
                })
            .RequireAuthorization(PrivacyPermissions.Erase)
            // A subject with a request open gets that request back, so a repeat leaves what the first left.
            .RetrySafe(RetrySafety.Convergent)
            .WithRequestExample(new RaiseErasureRequestCommand(Guid.Parse("0199b0c4-6f2e-7a31-8c5d-2e4f6a7b8c9d")))
            .WithName("RaiseErasureRequest");

        group
            .MapGet(
                "/{requestId:guid}",
                async (Guid requestId, IDispatcher dispatcher, CancellationToken ct) =>
                {
                    Result<ErasureRequestView> result =
                        await dispatcher.QueryAsync(new GetErasureRequestQuery(requestId), ct);

                    return result.ToHttpResult();
                })
            .RequireAuthorization(PrivacyPermissions.Erase)
            .Produces<ErasureRequestView>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithName("GetErasureRequest");
    }
}
