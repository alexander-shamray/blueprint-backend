using Microsoft.AspNetCore.Http.Timeouts;

namespace Gateway.Api;

/// <summary>Hands the edge's deadline back to <c>UseRequestTimeouts</c> as the cancellation it was (§9.7).</summary>
/// <remarks>The forwarder answers a cancelled request as a 400 itself, which §10.5's 504 has to replace.</remarks>
internal static class ProxyDeadline
{
    public static async Task RethrowAsync(HttpContext context, Func<Task> next)
    {
        await next();

        IHttpRequestTimeoutFeature? timeout = context.Features.Get<IHttpRequestTimeoutFeature>();
        if (timeout is { RequestTimeoutToken.IsCancellationRequested: true } && !context.Response.HasStarted)
        {
            throw new OperationCanceledException(context.RequestAborted);
        }
    }
}
