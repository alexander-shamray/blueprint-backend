using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace Common.Web;

/// <summary>The request body <see cref="RequestExampleExtensions.WithRequestExample{T}"/> declared.</summary>
public sealed record RequestExampleMetadata(object Example);

/// <summary>An example request body, declared beside the endpoint that binds it, for its document.</summary>
public static class RequestExampleExtensions
{
    /// <summary>The OpenAPI document, each declared example written into the operation that declared it.</summary>
    public static IServiceCollection AddCommonOpenApi(this IServiceCollection services) =>
        services.AddOpenApi(options => options.AddOperationTransformer<RequestExampleTransformer>());

    /// <summary>Declares <paramref name="example"/> as the body a caller of this endpoint might send.</summary>
    /// <remarks>Typed as the body, in <see cref="RequestExampleMetadata"/>, so a suite can validate it.</remarks>
    public static RouteHandlerBuilder WithRequestExample<T>(this RouteHandlerBuilder builder, T example)
        where T : notnull =>
        builder.WithMetadata(new RequestExampleMetadata(example));
}

/// <summary>Writes an endpoint's declared example into each media type of its request body.</summary>
internal sealed class RequestExampleTransformer(IOptions<JsonOptions> json) : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        RequestExampleMetadata? declared = context.Description.ActionDescriptor.EndpointMetadata
            .OfType<RequestExampleMetadata>()
            .LastOrDefault();

        if (declared is null || operation.RequestBody?.Content is not { } content)
            return Task.CompletedTask;

        // The host's own serialiser options, so the example is spelt as the endpoint reads a body.
        foreach (OpenApiMediaType media in content.Values)
        {
            media.Example = JsonSerializer.SerializeToNode(
                declared.Example,
                declared.Example.GetType(),
                json.Value.SerializerOptions);
        }

        return Task.CompletedTask;
    }
}
