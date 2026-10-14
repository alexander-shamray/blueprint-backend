namespace Notifications.Application.Records;

/// <summary>§5.6's repository for the notices this service owes, as its consumers write them.</summary>
public interface INotificationRepository
{
    /// <summary>Whether this event already owes this template's notice on a channel, as a redelivery finds.</summary>
    Task<bool> ExistsAsync(Guid eventId, string templateKey, string channel, CancellationToken ct);

    /// <summary>One notice by its id, tracked, for the send worker to move and commit.</summary>
    Task<Notification?> GetAsync(Guid notificationId, CancellationToken ct);

    void Add(Notification notification);
}
