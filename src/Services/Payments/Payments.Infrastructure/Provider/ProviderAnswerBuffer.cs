namespace Payments.Infrastructure.Provider;

/// <summary>Reads each answer whole inside the attempt, so a stalled body meets the timeout and the retries.</summary>
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
