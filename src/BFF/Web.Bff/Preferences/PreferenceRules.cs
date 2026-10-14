namespace Web.Bff.Preferences;

/// <summary>ADR-120's vocabulary, as this host checks a write against it before the hop.</summary>
/// <remarks>
/// Notifications' <c>ConsentVocabulary</c> owns each word and checks it again, since the BFF is not all that can reach
/// the method; these are its contract as preferences.proto's comments state it, not a second decision (ADR-120).
/// </remarks>
public static class PreferenceRules
{
    /// <summary>The seven order events, each a service topic, named by its template key.</summary>
    public static readonly IReadOnlyList<string> Topics =
    [
        "order-placed",
        "order-confirmed",
        "order-cancelled",
        "payment-declined",
        "payment-refunded",
        "shipment-dispatched",
        "shipment-delivered"
    ];

    public static readonly IReadOnlyList<string> Channels = ["email", "push"];
}
