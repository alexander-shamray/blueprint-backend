using Common.Application;
using Common.Web;
using FluentValidation;
using Web.Bff.Addresses;

namespace Web.Bff.Endpoints;

/// <summary>§10.9's saved delivery address, the caller's own and served from this host's schema.</summary>
public static class SavedAddressEndpoints
{
    public static void MapSavedAddressEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app
            .MapGroup("/v1/saved-address")
            .WithTags("SavedAddress")
            // Fail closed at the group (§11.4); the subject below is the principal's, never the request's.
            .RequireAuthorization();

        group
            .MapGet(
                "/",
                async (ICurrentUser user, SavedAddressStore store, CancellationToken ct) =>
                    (await store.FindAsync(user.Id, ct)).ToHttpResult())
            .WithName("GetSavedAddress");

        group
            .MapPut(
                "/",
                async (
                    SavedAddress request,
                    IValidator<SavedAddress> validator,
                    ICurrentUser user,
                    SavedAddressStore store,
                    CancellationToken ct) =>
                {
                    await validator.ValidateAndThrowAsync(request, ct);

                    SavedAddress address = request.Normalised();
                    await store.SaveAsync(user.Id, address, ct);

                    return Results.Ok(address);
                })
            // A PUT replaces: the same request leaves the row the first left (§8.5).
            .RetrySafe(RetrySafety.Convergent)
            .WithName("PutSavedAddress");

        group
            .MapDelete(
                "/",
                async (ICurrentUser user, SavedAddressStore store, CancellationToken ct) =>
                {
                    await store.DeleteAsync(user.Id, ct);

                    return Results.NoContent();
                })
            .RetrySafe(RetrySafety.Convergent)
            .WithName("DeleteSavedAddress");
    }
}
