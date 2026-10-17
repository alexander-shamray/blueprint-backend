using Common.Application;
using Common.Web;
using FluentValidation;

namespace Web.Bff.ProductImages;

/// <summary>E.6.11's upload endpoint: a pre-signed <c>PUT</c> for one key the server chose.</summary>
public static class ProductImageEndpoints
{
    public static void MapProductImageEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app
            .MapGroup("/v1/product-images")
            .WithTags("Product images")
            // Fail closed at the group (§11.4), and the permission publishing already requires (§10.8).
            .RequireAuthorization(BffPermissions.CatalogWrite);

        group
            .MapPost(
                "/",
                async (
                    ProductImageUploadRequest request,
                    IValidator<ProductImageUploadRequest> validator,
                    ICurrentUser user,
                    ProductImageUploads uploads,
                    CancellationToken ct) =>
                {
                    await validator.ValidateAndThrowAsync(request, ct);

                    // The seller is the principal's, and the request names none (§10.8).
                    return Results.Ok(uploads.Issue(user.Id, request.ContentType!, request.SizeBytes!.Value));
                })
            // Draws a key and signs a URL, and writes nothing: a repeat is a second URL for a second key (§8.5).
            .RetrySafe(RetrySafety.ReadOnly)
            .Produces<ProductImageUpload>()
            .WithName("IssueProductImageUpload");
    }
}
