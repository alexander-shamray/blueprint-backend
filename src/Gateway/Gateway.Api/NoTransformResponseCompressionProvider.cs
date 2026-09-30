using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Gateway.Api;

/// <summary>The framework's provider, refusing to compress when either side says no-transform (ADR-020).</summary>
internal sealed class NoTransformResponseCompressionProvider(
    IServiceProvider services,
    IOptions<ResponseCompressionOptions> options)
    : ResponseCompressionProvider(services, options)
{
    public override bool ShouldCompressResponse(HttpContext context)
    {
        // The request header selects the representation, so Vary carries it on every decision (ADR-020).
        AdvertiseVaryByCacheControl(context.Response);

        if (RefusesTransformation(context.Response.Headers.CacheControl) ||
            RefusesTransformation(context.Request.Headers.CacheControl))
        {
            return false;
        }

        return base.ShouldCompressResponse(context);
    }

    /// <summary>Adds <c>Cache-Control</c> to <c>Vary</c> unless it is there or <c>Vary</c> is the wildcard.</summary>
    private static void AdvertiseVaryByCacheControl(HttpResponse response)
    {
        foreach (string? value in response.Headers.GetCommaSeparatedValues(HeaderNames.Vary))
        {
            if (value == "*" || string.Equals(value, HeaderNames.CacheControl, StringComparison.OrdinalIgnoreCase))
                return;
        }

        response.Headers.Append(HeaderNames.Vary, HeaderNames.CacheControl);
    }

    /// <summary>A malformed <c>Cache-Control</c> says what an absent one does, so it is not a refusal.</summary>
    private static bool RefusesTransformation(StringValues cacheControl) =>
        CacheControlHeaderValue.TryParse(cacheControl.ToString(), out CacheControlHeaderValue? parsed) &&
        parsed.NoTransform;
}
