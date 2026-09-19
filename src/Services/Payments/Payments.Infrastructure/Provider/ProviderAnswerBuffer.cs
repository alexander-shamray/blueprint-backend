namespace Payments.Infrastructure.Provider;

/// <summary>
/// Reads each answer whole inside the attempt. <c>HttpClient</c> otherwise
/// buffers the body after the resilience pipeline has returned, so a provider
/// that sent its headers and then stalled or broke off the body would escape
/// the attempt timeout, the retries and the counter (<see cref="ProviderHop"/>).
/// </summary>
internal sealed class ProviderAnswerBuffer : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage response = await base.SendAsync(request, ct);

        try
        {
            await response.Content.LoadIntoBufferAsync(ProviderHop.MaxAnswerBytes, ct);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }
}
