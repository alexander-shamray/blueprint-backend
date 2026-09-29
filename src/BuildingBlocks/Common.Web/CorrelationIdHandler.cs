using Microsoft.AspNetCore.Http;

namespace Common.Web;

/// <summary>
/// §10.4's outbound half: copies this request's correlation ID onto a call
/// this host makes to a peer.
/// </summary>
/// <remarks>
/// Here rather than in a host that calls a peer (§9.7, ADR-017), because the
/// guarantee is §10.4's; a host attaches it to the named client that needs it.
/// With no inbound ID it sends no header and the callee mints one from its
/// trace — the answer for work that begins at a claimed row, not a request.
/// </remarks>
public sealed class CorrelationIdHandler(IHttpContextAccessor context) : DelegatingHandler
{
    // cancellationToken rather than this repository's usual ct: CA1725 requires
    // an override to keep the base declaration's parameter name, and ADR-019
    // makes that an error. The same correction ClientCredentialsHandler
    // carries.
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string? correlationId = context.HttpContext?.Request
            .Headers[CorrelationIdExtensions.Header]
            .FirstOrDefault();

        // Set rather than added: a retried attempt runs this handler again on
        // the same HttpRequestMessage, and Add would accumulate one value per
        // attempt into a header the callee reads with FirstOrDefault.
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            request.Headers.Remove(CorrelationIdExtensions.Header);
            request.Headers.Add(CorrelationIdExtensions.Header, correlationId);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
