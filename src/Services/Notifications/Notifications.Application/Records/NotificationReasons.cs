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

    /// <summary>Every reason an undeliverable row may carry, so a typo is refused rather than stored.</summary>
    public static IReadOnlySet<string> All { get; } =
        new HashSet<string>(StringComparer.Ordinal) { NoSuchCustomer, RecipientRefused, NotAMailbox, GaveUp, Erased };
}
