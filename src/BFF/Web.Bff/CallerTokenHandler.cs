using Microsoft.Net.Http.Headers;

namespace Web.Bff;

/// <summary>ADR-117's outbound credential: the caller's own access token, copied unchanged onto the hop.</summary>
/// <remarks>
/// In place of <c>ClientCredentialsHandler</c>, so Notifications takes the subject from the buyer's token and no
/// credential this host holds can register a device to anyone else (§11.5).
/// </remarks>
internal sealed class CallerTokenHandler(IHttpContextAccessor context) : DelegatingHandler
{
    // cancellationToken rather than ct: CA1725 keeps the base parameter name (ADR-019).
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // Thrown rather than sent bare: the route requires a signed-in caller, so a missing header is a defect here,
        // and a call without one would read as Notifications refusing the buyer.
        string authorization = context.HttpContext?.Request.Headers[HeaderNames.Authorization].FirstOrDefault()
            ?? throw new InvalidOperationException(
                "CallerTokenHandler found no inbound Authorization header to forward; it serves only a call made " +
                "inside an authenticated request (ADR-117).");

        // Set rather than added, because a retried attempt runs this handler again on the same request.
        request.Headers.Remove(HeaderNames.Authorization);
        request.Headers.TryAddWithoutValidation(HeaderNames.Authorization, authorization);

        return base.SendAsync(request, cancellationToken);
    }
}
