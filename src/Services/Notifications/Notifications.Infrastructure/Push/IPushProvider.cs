using Notifications.Application.Push;

namespace Notifications.Infrastructure.Push;

/// <summary>One provider behind <see cref="IPushChannel"/>, with its own client, hop and breaker.</summary>
internal interface IPushProvider
{
    /// <summary>The device row's platform this provider delivers to, <c>fcm</c> or <c>apns</c>.</summary>
    string Platform { get; }

    /// <summary>Its pipeline, whose breaker parks this platform's rows alone.</summary>
    PushPipeline Pipeline { get; }

    Task<PushResult> SendAsync(OutboundPush push, CancellationToken ct);
}
