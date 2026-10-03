namespace Notifications.Infrastructure.Contacts;

/// <summary>Reads each answer whole inside the attempt, so a stalled or oversize body meets the budget.</summary>
internal sealed class ContactAnswerBuffer : DelegatingHandler
{
    /// <remarks><c>cancellationToken</c>, not <c>ct</c>: CA1725 keeps the base name (ADR-019).</remarks>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await base.SendAsync(request, cancellationToken);

        try
        {
            await response.Content.LoadIntoBufferAsync(ContactHop.MaxAnswerBytes, cancellationToken);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }
}
