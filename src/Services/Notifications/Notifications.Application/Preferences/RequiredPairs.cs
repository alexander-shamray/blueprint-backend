namespace Notifications.Application.Preferences;

/// <summary>The pairs a jurisdiction obliges the service to send, which no preference switches off (ADR-120).</summary>
/// <remarks>
/// Built by Infrastructure from <c>NotificationsJurisdictionOptions.Required</c> once start-up has refused any
/// pair outside the service class, so every pair here is a service topic's.
/// </remarks>
public sealed class RequiredPairs(IEnumerable<ConsentPair> pairs)
{
    private readonly HashSet<ConsentPair> _pairs = [.. pairs];

    public bool Contains(ConsentPair pair) => _pairs.Contains(pair);
}
