namespace Platform.IntegrationTests.Journey;

/// <summary>
/// One deployment's answers to ADR-053's questions and the address its customer lives at, so the same journey can be
/// run under a second set and the difference between the two is the proof (§12.1).
/// </summary>
/// <remarks>
/// Both are made up, in values no real deployment would choose, so a journey passing under either read its
/// configuration rather than a constant. They differ in every member, the country among them.
/// </remarks>
public sealed record Jurisdiction(
    string Name,
    string Country,
    string Currency,
    string City,
    string PostalCode,
    string Locale,
    IReadOnlyList<string> Languages,
    string TimeZone,
    string LogRetention,
    string ContactRetention,
    string OrderRetention,
    string NotificationsGiveUp,
    string AddressRetention,
    string TrackingRetention,
    string ShippingGiveUp)
{
    /// <summary>The set the service suites' hosts default to: Kazakh first, Chatham's offset, tenge.</summary>
    public static Jurisdiction First { get; } = new(
        "first",
        "KZ",
        "KZT",
        "Almaty",
        "050000",
        "kk",
        ["kk", "en"],
        "Pacific/Chatham",
        "1013.00:00:00",
        "17.00:00:00",
        "71.00:00:00",
        "2.07:00:00",
        "11.00:00:00",
        "23.00:00:00",
        "5.07:00:00");

    /// <summary>A second set that agrees with the first on nothing a host reads or a customer sees.</summary>
    public static Jurisdiction Second { get; } = new(
        "second",
        "NL",
        "EUR",
        "Utrecht",
        "3511 AB",
        "ru",
        ["ru", "en"],
        "Pacific/Pago_Pago",
        "977.00:00:00",
        "19.00:00:00",
        "67.00:00:00",
        "3.11:00:00",
        "13.00:00:00",
        "29.00:00:00",
        "6.05:00:00");
}
