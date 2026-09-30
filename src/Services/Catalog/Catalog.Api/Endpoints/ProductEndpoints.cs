using Catalog.Application.Products.GetProducts;
using Catalog.Application.Products.PublishProduct;
using Common.Application;
using Common.Web;

namespace Catalog.Api.Endpoints;

/// <summary>
/// One static class per aggregate (ADR-015), in the namespace §4.2's gate selects on; the gateway strips
/// <c>/api</c> (§10.2), so these routes start at the version.
/// </summary>
public static class ProductEndpoints
{
    public static void MapProductEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app
            .MapGroup("/v1/catalog/products")
            .WithTags("Products")
            // Fail closed at the group (§11.4), so a new endpoint arrives unreachable rather than public.
            .RequireAuthorization();

        // Bound straight from the body, since the wire shape and the command are identical primitives.
        group
            .MapPost(
                "/",
                async (PublishProductCommand command, IDispatcher dispatcher, CancellationToken ct) =>
                {
                    Result<Guid> result = await dispatcher.SendAsync(command, ct);

                    return result.ToHttpResult();
                })
            .RequireAuthorization(CatalogPermissions.Write)
            .WithName("PublishProduct");

        // CursorPage, not Result (§6.2), so ToHttpResult has no part here.
        group
            .MapGet(
                "/",
                async (string? cursor, IDispatcher dispatcher, CancellationToken ct, int limit = 20) =>
                {
                    CursorPage<ProductSummaryDto> page =
                        await dispatcher.QueryAsync(new GetProductsQuery(cursor, limit), ct);

                    return Results.Ok(page);
                })
            // Anonymous, as §10.2's catalog-public route specifies, and stated because the group fails closed.
            .AllowAnonymous()
            .WithName("GetProducts");
    }
}
