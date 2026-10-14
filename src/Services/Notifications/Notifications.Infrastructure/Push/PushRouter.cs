using System.Collections.Frozen;
using Notifications.Application.Push;

namespace Notifications.Infrastructure.Push;

/// <summary>The one <see cref="IPushChannel"/>, choosing a provider by the device row's platform.</summary>
/// <remarks>So the send path names no provider, and each provider stays behind its own hop and breaker.</remarks>
internal sealed class PushRouter(IEnumerable<IPushProvider> providers) : IPushChannel
{
    private readonly FrozenDictionary<string, IPushProvider> _byPlatform =
        providers.ToFrozenDictionary(p => p.Platform, StringComparer.Ordinal);

    public Task<PushResult> SendAsync(OutboundPush push, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(push);

        // Registration refuses an app this deployment does not name, so only a list that changed since reaches here.
        return _byPlatform.TryGetValue(push.Platform, out IPushProvider? provider)
            ? provider.SendAsync(push, ct)
            : throw new PushUnavailableException(
                $"Delivery {push.DeliveryId} names a platform no app of this deployment uses.",
                PushFault.Rejected,
                httpStatus: null);
    }
}
