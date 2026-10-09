namespace Platform.IntegrationTests.Journey;

/// <summary>One deployment's answers (ADR-053) and its customer's address, so a journey can run twice.</summary>
/// <remarks>
/// Both sets are made up and differ in every member, so a run passing under either read its configuration (§12.1).
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
