using Notifications.Application.Devices;

namespace Notifications.Application.Records;

/// <summary>One push notice's send to one installation, ended alone (the push design's section 3).</summary>
/// <remarks>
/// It holds no token, so the send reads the device row's own and honours a rotation (ADR-117), and no parameters,
/// which its notice keeps. Each move returns whether it moved the row, as <see cref="Notification"/>'s do.
/// </remarks>
public sealed class PushDelivery
{
    public Guid DeliveryId { get; private set; }

    public Guid NotificationId { get; private set; }

    public Guid InstallationId { get; private set; }

    /// <summary>The device row's platform at the fan-out, which picks the provider and its breaker.</summary>
    public string Platform { get; private set; } = "";

    /// <summary>The notice's customer, copied so an erasure finds the row without its notice (§11.7).</summary>
    public Guid? CustomerId { get; private set; }

    /// <summary>The journal entry the send was decided under; null where the default was (ADR-120).</summary>
    public Guid? ConsentEntryId { get; private set; }

    /// <summary>The push template's version, stamped with the intent so the row names what was sent.</summary>
    public int? TemplateVersion { get; private set; }

    /// <summary>The languages the fan-out chose from the customer's locale, which the send renders in.</summary>
    public string? Languages { get; private set; }

    public NotificationStatus Status { get; private set; }

    public string? Reason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>The intent, committed before the provider is called, so a resend is a row that carries it.</summary>
    public DateTimeOffset? SendStartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset NextAttemptAt { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }

    /// <summary>The notice's, which every push sent for this row carries (§10.4).</summary>
    public Guid CorrelationId { get; private set; }

    // EF Core materialisation only (§5.4).
    private PushDelivery() { }

    private PushDelivery(
        Notification notice,
        Guid installationId,
        string platform,
        Guid? consentEntryId,
        string languages,
        DateTimeOffset now)
    {
        DeliveryId = Guid.CreateVersion7();
        NotificationId = notice.NotificationId;
        InstallationId = installationId;
        Platform = platform;
        CustomerId = notice.CustomerId;
        ConsentEntryId = consentEntryId;
        Languages = languages;
        Status = NotificationStatus.Pending;
        CreatedAt = now;
        NextAttemptAt = now;
        CorrelationId = notice.CorrelationId;
    }

    /// <summary>The fan-out's row for one device the notice's customer holds.</summary>
    public static PushDelivery For(
        Notification notice,
        Guid installationId,
        string platform,
        Guid? consentEntryId,
        string languages,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(notice);
        ArgumentOutOfRangeException.ThrowIfEqual(installationId, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(languages);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(languages.Length, NotificationLimits.MaxLanguagesLength);

        if (!DeviceLimits.Platforms.Contains(platform))
            throw new ArgumentOutOfRangeException(nameof(platform), platform, "Not one of DeviceLimits.Platforms.");

        // A delivery is only ever fanned out from a push notice whose customer is committed (the push design).
        if (notice.CustomerId is null)
            throw new InvalidOperationException($"Notification {notice.NotificationId} names no customer yet.");

        return new PushDelivery(notice, installationId, platform, consentEntryId, languages, now);
    }

    /// <summary>The subject's newest entry withdrew the pair since the fan-out, so nothing is sent (ADR-120).</summary>
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

    /// <summary>A terminal answer whose reason is one of <see cref="NotificationReasons.Delivery"/>.</summary>
    public bool MarkUndeliverable(string reason, DateTimeOffset now)
    {
        if (!NotificationReasons.Delivery.Contains(reason))
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "Not one of NotificationReasons.Delivery.");

        if (Status != NotificationStatus.Pending)
            return false;

        Status = NotificationStatus.Undeliverable;
        Reason = reason;
        CompletedAt = now;
        return true;
    }

    /// <summary>The intent, before the call: the version and the entry the send ran under, and when.</summary>
    /// <remarks>Once only, as a notice's is: a row claimed with the intent set is resent under it (ADR-052).</remarks>
    public bool StartSend(int templateVersion, Guid? consentEntryId, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(templateVersion, 1);

        if (Status != NotificationStatus.Pending || SendStartedAt is not null)
            return false;

        TemplateVersion = templateVersion;
        ConsentEntryId = consentEntryId;
        SendStartedAt = now;
        return true;
    }

    /// <summary>The provider accepted the push; only a row whose intent was committed first can be sent.</summary>
    public bool MarkSent(DateTimeOffset now)
    {
        if (Status != NotificationStatus.Pending || SendStartedAt is null)
            return false;

        Status = NotificationStatus.Sent;
        CompletedAt = now;
        return true;
    }
}
