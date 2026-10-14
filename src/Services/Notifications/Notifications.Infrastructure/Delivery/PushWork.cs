namespace Notifications.Infrastructure.Delivery;

/// <summary>One leased delivery: what a push pass decides on, and the stamp a resend renders again.</summary>
/// <remarks>
/// No token is projected, as the row holds none: the pass reads the device row's own (ADR-117). The trace pair is
/// the fan-out's, which the pass restores as its parent (§9.4); <c>LockedUntil</c> is the lease its backoff names.
/// </remarks>
public sealed record PushWork(
    Guid DeliveryId,
    Guid NotificationId,
    Guid InstallationId,
    string Platform,
    Guid? CustomerId,
    Guid? ConsentEntryId,
    int? TemplateVersion,
    string? Languages,
    DateTimeOffset? SendStartedAt,
    Guid CorrelationId,
    string? TraceParent,
    string? TraceState,
    DateTimeOffset LockedUntil);
