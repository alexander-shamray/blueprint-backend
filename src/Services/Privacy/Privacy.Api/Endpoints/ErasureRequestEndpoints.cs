using Common.Application;
using Common.Web;
using Privacy.Application.ErasureRequests.AmendResponders;
using Privacy.Application.ErasureRequests.GetErasureRequest;
using Privacy.Application.ErasureRequests.RaiseErasureRequest;
using Privacy.Application.ErasureRequests.ReissueErasureRequest;

namespace Privacy.Api.Endpoints;

/// <summary>
/// One static class per aggregate (ADR-015); the gateway strips <c>/api</c> (§10.2), so routes start at the version.
/// </summary>
/// <remarks>The gateway's privacy-erase route admits holders of <c>privacy:erase</c> to this group (§10.2).</remarks>
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

        // Asks the holders again under the same request (ADR-092). Keyed, since a repeat would broadcast again.
        group
            .MapPost(
                "/{requestId:guid}/reissue",
                async (
                    Guid requestId,
                    ReissueErasureRequestRequest request,
                    IDispatcher dispatcher,
                    CancellationToken ct) =>
                {
                    Result result = await dispatcher.SendAsync(
                        new ReissueErasureRequestCommand(request.CommandId, requestId),
                        ct);

                    return result.ToHttpResult();
                })
            .RequireAuthorization(PrivacyPermissions.Erase)
            // Built from the route and the body, so no parameter names the command (§8.5).
            .Idempotent<ReissueErasureRequestCommand>()
            .WithRequestExample(new ReissueErasureRequestRequest(Guid.Parse("0199b0c4-8b21-7c53-ae7f-4a6b8c9d0e1f")))
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("ReissueErasureRequest");

        // Changes the set of a request that has not closed, under the caller's name (ADR-136). Keyed, since a repeat
        // would record a second amendment; it publishes nothing, so a holder it adds is asked by a reissue.
        group
            .MapPost(
                "/{requestId:guid}/responders",
                async (
                    Guid requestId,
                    AmendErasureRespondersRequest request,
                    IDispatcher dispatcher,
                    CancellationToken ct) =>
                {
                    Result result = await dispatcher.SendAsync(
                        new AmendErasureRespondersCommand(
                            request.CommandId,
                            requestId,
                            request.Add ?? [],
                            request.Remove ?? [],
                            request.Reason),
                        ct);

                    return result.ToHttpResult();
                })
            .RequireAuthorization(PrivacyPermissions.Erase)
            .Idempotent<AmendErasureRespondersCommand>()
            .WithRequestExample(
                new AmendErasureRespondersRequest(
                    Guid.Parse("0199b0c4-9c32-7d64-bf80-5b7c9d0e1f2a"),
                    ["shipping"],
                    ["shiping"],
                    "privacy.responders misspelt shipping when the request was raised"))
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithName("AmendErasureResponders");

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

/// <summary>The body of a reissue; the request is the route's, so the body carries the key alone.</summary>
public sealed record ReissueErasureRequestRequest(Guid CommandId);

/// <summary>The body of an amendment; either list may be left out, and the reason is required.</summary>
public sealed record AmendErasureRespondersRequest(
    Guid CommandId,
    IReadOnlyList<string>? Add,
    IReadOnlyList<string>? Remove,
    string Reason);
