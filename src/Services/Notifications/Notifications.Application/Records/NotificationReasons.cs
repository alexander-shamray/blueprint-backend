namespace Notifications.Application.Records;

/// <summary>Why a notification is undeliverable, a closed set ADR-052's outcomes and §11.7's erasure name.</summary>
public static class NotificationReasons
{
    public const string NoSuchCustomer = "no_such_customer";
    public const string RecipientRefused = "recipient_refused";

    /// <summary>The contact's mailbox does not parse or carries a line break, so nothing was sent.</summary>
    public const string NotAMailbox = "not_a_mailbox";

    public const string GaveUp = "gave_up";
    public const string Erased = "erased";

    /// <summary>A suppressed row's reason when the journal withdrew its pair (ADR-120); never undeliverable.</summary>
    public const string OptedOut = "opted_out";

    /// <summary>A push notice whose subject holds no device, or a delivery whose device left it (ADR-117).</summary>
    public const string NoDevice = "no_device";

    /// <summary>A provider's <c>PushRefusal.Unregistered</c>, the one answer that deletes the device row.</summary>
    public const string Unregistered = "unregistered";

    /// <summary>A provider's <c>PushRefusal.InvalidToken</c>, which keeps the device row (ADR-121).</summary>
    public const string InvalidToken = "invalid_token";

    /// <summary>Every reason an undeliverable notice may carry, so a typo is refused rather than stored.</summary>
    public static IReadOnlySet<string> All { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            NoSuchCustomer, RecipientRefused, NotAMailbox, GaveUp, Erased, NoDevice
        };

    /// <summary>Every reason an undeliverable push delivery may carry (the push design's section 3).</summary>
    public static IReadOnlySet<string> Delivery { get; } =
        new HashSet<string>(StringComparer.Ordinal) { Unregistered, InvalidToken, NoDevice, GaveUp };
}
