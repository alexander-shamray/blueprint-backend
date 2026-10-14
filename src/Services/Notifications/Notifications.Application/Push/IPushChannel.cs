namespace Notifications.Application.Push;

/// <summary>The push providers in this service's words, and the one place an adapter goes (§3.1).</summary>
/// <remarks>
/// A fault throws <see cref="PushUnavailableException"/> and is never a <see cref="PushResult"/>, so a provider's
/// outage cannot reach a row as a refusal; no provider's type reaches above this port.
/// </remarks>
public interface IPushChannel
{
    Task<PushResult> SendAsync(OutboundPush push, CancellationToken ct);
}
