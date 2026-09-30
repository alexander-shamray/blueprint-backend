using Microsoft.AspNetCore.Http;

namespace Common.Web;

/// <summary>§10.4's outbound half: copies this request's correlation ID onto a call to a peer.</summary>
public sealed class CorrelationIdHandler(IHttpContextAccessor context) : DelegatingHandler
{
    // cancellationToken rather than ct: CA1725 keeps the base parameter name (ADR-019).
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string? correlationId = context.HttpContext?.Request
            .Headers[CorrelationIdExtensions.Header]
            .FirstOrDefault();

        // Set rather than added, because a retried attempt runs this handler again on the same request.
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            request.Headers.Remove(CorrelationIdExtensions.Header);
            request.Headers.Add(CorrelationIdExtensions.Header, correlationId);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
