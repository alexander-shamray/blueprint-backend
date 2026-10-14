using Notifications.Application.Preferences;

namespace Notifications.Application.Delivery;

/// <summary>The channels a deployment writes a notice on per event: mail always, push once an app is named.</summary>
/// <remarks>
/// With no app named no device row can exist, as registration refuses an app the deployment does not name (ADR-117),
/// so a push notice would only ever end <c>no_device</c>; the deployment that names its first app writes them.
/// </remarks>
public sealed class DeliveryChannels(bool push)
{
    /// <summary>Whether the deployment names a push app.</summary>
    public bool Push { get; } = push;

    /// <summary>Mail first, then push, so a reader of the record finds the mail notice the older rows match.</summary>
    public IReadOnlyList<string> All { get; } =
        push ? [ConsentVocabulary.Email, ConsentVocabulary.Push] : [ConsentVocabulary.Email];
}
