using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Common.Web;

/// <summary>Translates <see cref="BadHttpRequestException"/> into §10.5's unreadable-request row.</summary>
/// <remarks>No message is copied and the pointer is rebuilt from the endpoint's contract (§10.5).</remarks>
internal sealed partial class BadHttpRequestExceptionHandler(
    IProblemDetailsService problemDetails,
    IOptions<JsonOptions> json) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        // A status outside 4xx would blame the client for whatever constructed it, so it stays the fallback's 500.
        if (exception is not BadHttpRequestException { StatusCode: >= 400 and < 500 } refused)
            return false;

        httpContext.Response.StatusCode = refused.StatusCode;

        ProblemDetails problem = new()
        {
            Status = refused.StatusCode,
            Detail = "The request could not be read as this endpoint expects it, so nothing was done.",
            Extensions = { ["code"] = "request.unreadable" }
        };

        Type? body = httpContext.Features
            .Get<IExceptionHandlerFeature>()?.Endpoint?.Metadata
            .GetMetadata<IAcceptsMetadata>()?.RequestType;

        if (refused.InnerException is JsonException { Path: { } path } && body is not null &&
            PointerTo(path, body, json.Value.SerializerOptions) is { } pointer)
        {
            problem.Extensions["pointer"] = pointer;
        }

        await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem
        });

        return true;
    }

    /// <summary>The longest prefix of the reader's path the body declares, as a URI-fragment JSON pointer.</summary>
    private static string? PointerTo(string path, Type body, JsonSerializerOptions options)
    {
        // The leading `$` names the whole body, which is no member to point at.
        if (!path.StartsWith('$') || !options.TryGetTypeInfo(body, out JsonTypeInfo? node))
            return null;

        StringComparison names = options.PropertyNameCaseInsensitive
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        StringBuilder pointer = new("#");

        foreach (Match segment in Segment().Matches(path, 1))
        {
            string? next = null;
            Type? type = null;

            if (segment.Groups["index"].Success && node.Kind == JsonTypeInfoKind.Enumerable)
            {
                next = segment.Groups["index"].Value;
                type = node.ElementType;
            }
            else if (segment.Groups["name"].Success && node.Kind == JsonTypeInfoKind.Object)
            {
                JsonPropertyInfo? declared = node.Properties.FirstOrDefault(p =>
                    string.Equals(p.Name, segment.Groups["name"].Value, names));
                next = declared?.Name;
                type = declared?.PropertyType;
            }

            if (next is null || type is null)
                break;

            pointer.Append('/').Append(Uri.EscapeDataString(next.Replace("~", "~0").Replace("/", "~1")));

            if (!options.TryGetTypeInfo(type, out node))
                break;
        }

        return pointer.Length > 1 ? pointer.ToString() : null;
    }

    // System.Text.Json's path syntax: `.name`, `['name']` for a name needing quotes, `[n]` for an index.
    [GeneratedRegex(
        @"\G(?:\.(?<name>[^.\[]+)|\['(?<name>(?:[^'\\]|\\.)*)'\]|\[(?<index>[0-9]+)\])",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex Segment();
}
