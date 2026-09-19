using System.Net;

namespace Payments.Infrastructure.Provider;

internal sealed class ProviderAttemptCounter(ProviderMetrics metrics) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            HttpResponseMessage response = await base.SendAsync(request, ct);

            if (IsTransient(response.StatusCode))
                metrics.Unavailable();

            return response;
        }
        catch (HttpRequestException)
        {
            // A refused or broken connection is the provider's. A cancelled
            // attempt is not counted here: an attempt timeout and the caller's
            // own cancellation arrive as the same exception, so timeouts are
            // counted where only they arrive, the pipeline's OnTimeout.
            metrics.Unavailable();
            throw;
        }
    }

    internal static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;
}
