namespace Notifications.Application.Records;

/// <summary>§5.6's repository for the notices this service owes, as its consumers write them.</summary>
public interface INotificationRepository
{
    /// <summary>Whether this event already owes this template's notice, as a redelivery past the inbox finds.</summary>
    Task<bool> ExistsAsync(Guid eventId, string templateKey, CancellationToken ct);

    void Add(Notification notification);
}
