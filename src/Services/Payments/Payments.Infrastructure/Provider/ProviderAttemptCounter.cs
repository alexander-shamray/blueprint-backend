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
            // Not a cancellation, which may be the caller's; timeouts are counted in the pipeline's OnTimeout.
            metrics.Unavailable();
            throw;
        }
    }

    internal static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;
}
