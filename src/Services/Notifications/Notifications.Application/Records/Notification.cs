using Notifications.Application.Preferences;

namespace Notifications.Application.Records;

/// <summary>One row of §3.2's <c>NotificationLog</c>: a notice owed for one event, and what became of it.</summary>
/// <remarks>
/// ADR-053 rule 4's record, naming nobody but the customer's id. Each move returns whether it moved the row, and
/// an arrival the row has outgrown returns false, because a throw is a row its worker retries for ever (ADR-052).
/// </remarks>
public sealed class Notification
{
    public Guid NotificationId { get; private set; }

    /// <summary>The event the consumer wrote this row for; with the key it is unique (§9.5's second line).</summary>
    public Guid EventId { get; private set; }

    /// <summary>The event's own, which every message sent for this row carries (§10.4).</summary>
    public Guid CorrelationId { get; private set; }

    public string TemplateKey { get; private set; } = "";

    /// <summary><c>email</c> or <c>push</c>: one notice per event, key and channel (the push design).</summary>
    public string Channel { get; private set; } = "";

    public Guid OrderId { get; private set; }

    /// <summary>Copied from the order record when the worker resolves it, and null until then (ADR-053).</summary>
    public Guid? CustomerId { get; private set; }

    /// <summary>The version rendered, stamped with the intent so the row names what was sent (ADR-053).</summary>
    public int? TemplateVersion { get; private set; }

    public string? Languages { get; private set; }

    /// <summary>The journal entry a send or an opt-out was decided under; null where a default or a duty was.</summary>
    /// <remarks>Stamped once, with the intent or the opt-out, so the row shows its consent (ADR-120).</remarks>
    public Guid? ConsentEntryId { get; private set; }

    /// <summary>The values the template's placeholders take, as their own writer formats them.</summary>
    public string Parameters { get; private set; } = "";

    public NotificationStatus Status { get; private set; }

    public string? Reason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>The intent, committed before the send, so a resend is a row that already carries it.</summary>
    public DateTimeOffset? SendStartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset NextAttemptAt { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }

    // EF Core materialisation only (§5.4).
    private Notification() { }

    private Notification(
        Guid eventId,
        Guid correlationId,
        string templateKey,
        string channel,
        Guid orderId,
        string parameters,
        DateTimeOffset now)
    {
        NotificationId = Guid.CreateVersion7();
        EventId = eventId;
        CorrelationId = correlationId;
        TemplateKey = templateKey;
        Channel = channel;
        OrderId = orderId;
        Parameters = parameters;
        Status = NotificationStatus.Pending;
        CreatedAt = now;
        NextAttemptAt = now;
    }

    /// <summary>An event's consumer owes a notice on one channel: the record's first row.</summary>
    public static Notification Pending(
        Guid eventId,
        Guid correlationId,
        string templateKey,
        string channel,
        Guid orderId,
        string parameters,
        DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(eventId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(correlationId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(orderId, Guid.Empty);
        Require(templateKey, NotificationLimits.MaxTemplateKeyLength, nameof(templateKey));
        Require(parameters, NotificationLimits.MaxParametersLength, nameof(parameters));

        if (!ConsentVocabulary.IsChannel(channel))
            throw new ArgumentOutOfRangeException(nameof(channel), channel, "Not one of ConsentVocabulary.Channels.");

        return new Notification(eventId, correlationId, templateKey, channel, orderId, parameters, now);
    }

    /// <summary>The order record named the customer; a second answer does not overwrite the first.</summary>
    public bool AssignCustomer(Guid customerId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(customerId, Guid.Empty);

        if (Status != NotificationStatus.Pending || CustomerId is not null)
            return false;

        CustomerId = customerId;
        return true;
    }

    /// <summary>A decline whose order the customer cancelled is never sent (ADR-049).</summary>
    public bool Suppress(DateTimeOffset now)
    {
        if (Status != NotificationStatus.Pending)
            return false;

        Status = NotificationStatus.Suppressed;
        CompletedAt = now;
        return true;
    }

    /// <summary>The subject's newest entry for the pair withdrew it, so nothing is fetched or sent (ADR-120).</summary>
    public bool OptOut(Guid? consentEntryId, DateTimeOffset now)
    {
        if (Status != NotificationStatus.Pending || SendStartedAt is not null)
            return false;

        Status = NotificationStatus.Suppressed;
        Reason = NotificationReasons.OptedOut;
        ConsentEntryId = consentEntryId;
        CompletedAt = now;
        return true;
    }

    /// <summary>A terminal answer whose reason is one of <see cref="NotificationReasons.All"/>.</summary>
    public bool MarkUndeliverable(string reason, DateTimeOffset now)
    {
        if (!NotificationReasons.All.Contains(reason))
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "Not one of NotificationReasons.All.");

        if (Status != NotificationStatus.Pending)
            return false;

        Status = NotificationStatus.Undeliverable;
        Reason = reason;
        CompletedAt = now;
        return true;
    }

    /// <summary>The intent, before the send: the version, languages and consent entry it ran under, and when.</summary>
    /// <remarks>Once only: a row claimed with the intent set is resent under it, not restamped (ADR-052).</remarks>
    public bool StartSend(int templateVersion, string languages, Guid? consentEntryId, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(templateVersion, 1);
        Require(languages, NotificationLimits.MaxLanguagesLength, nameof(languages));

        if (Status != NotificationStatus.Pending || SendStartedAt is not null)
            return false;

        TemplateVersion = templateVersion;
        Languages = languages;
        ConsentEntryId = consentEntryId;
        SendStartedAt = now;
        return true;
    }

    /// <summary>A push notice fanned out to its deliveries, whose outcomes are now its own (the push design).</summary>
    /// <remarks>Stamped with the entry the fan-out was decided under, as a send's intent is (ADR-120).</remarks>
    public bool Dispatch(string languages, Guid? consentEntryId, DateTimeOffset now)
    {
        Require(languages, NotificationLimits.MaxLanguagesLength, nameof(languages));

        if (Status != NotificationStatus.Pending || Channel != ConsentVocabulary.Push)
            return false;

        Status = NotificationStatus.Dispatched;
        Languages = languages;
        ConsentEntryId = consentEntryId;
        CompletedAt = now;
        return true;
    }

    /// <summary>The relay accepted the message; only a row whose intent was committed first can be sent.</summary>
    public bool MarkSent(DateTimeOffset now)
    {
        if (Status != NotificationStatus.Pending || SendStartedAt is null)
            return false;

        Status = NotificationStatus.Sent;
        CompletedAt = now;
        return true;
    }

    // A malformed value is the caller's defect rather than an arrival to absorb, so it throws.
    private static void Require(string value, int maxLength, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value.Length, maxLength, name);
    }
}
