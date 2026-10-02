namespace Notifications.Application.Records;

/// <summary>Where a notification stands; every state but <c>Pending</c> is terminal (ADR-052).</summary>
/// <remarks>Stored by name, never by number (§7.2), so the member order is no storage contract.</remarks>
public enum NotificationStatus
{
    Pending,
    Sent,
    Suppressed,
    Undeliverable,
}
