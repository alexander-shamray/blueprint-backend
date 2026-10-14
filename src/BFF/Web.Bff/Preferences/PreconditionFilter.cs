using FluentValidation;
using FluentValidation.Results;
using Microsoft.Net.Http.Headers;

namespace Web.Bff.Preferences;

/// <summary>The version a write read: an entry's id, or none for <c>If-None-Match: *</c>.</summary>
public sealed record Precondition(Guid? ReadVersion);

/// <summary>§10.5's precondition filter: a write names the version it read, or it is refused as malformed.</summary>
/// <remarks>
/// ADR-120's two forms and no others: one strong entity tag holding an entry's id in <c>If-Match</c>, or
/// <c>If-None-Match: *</c> for a pair with no entry. Whether the precondition holds is Notifications' to decide, under
/// the lock it takes; this filter only reads it. A refusal is §10.5's 400 keyed by the header, quoting no value.
/// </remarks>
internal sealed class PreconditionFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        IHeaderDictionary headers = context.HttpContext.Request.Headers;

        context.HttpContext.Features.Set(
            Read(headers[HeaderNames.IfMatch].ToString(), headers[HeaderNames.IfNoneMatch].ToString()));

        return await next(context);
    }

    /// <summary>The precondition the two headers state, each read as one comma-joined value.</summary>
    internal static Precondition Read(string ifMatch, string ifNoneMatch)
    {
        bool match = ifMatch.Length > 0;
        bool noneMatch = ifNoneMatch.Length > 0;

        if (match && noneMatch)
            throw Refused(HeaderNames.IfMatch, "send If-Match or If-None-Match, not both.");

        if (noneMatch)
        {
            return ifNoneMatch.Trim() == "*"
                ? new Precondition(null)
                : throw Refused(HeaderNames.IfNoneMatch, "must be *, for a preference that has no version yet.");
        }

        if (!match)
        {
            throw Refused(
                HeaderNames.IfMatch,
                "is required: send the version read, or If-None-Match: * for a preference that has none.");
        }

        // One strong tag; a weak one never matches under If-Match's strong comparison, and * names no version.
        string tag = ifMatch.Trim();

        return tag is ['"', .. var inner, '"'] && Guid.TryParseExact(inner, "D", out Guid version)
            ? new Precondition(version)
            : throw Refused(HeaderNames.IfMatch, "must be one quoted version, as a preference read returns it.");
    }

    private static ValidationException Refused(string header, string message) =>
        new([new ValidationFailure(header, message)]);
}
