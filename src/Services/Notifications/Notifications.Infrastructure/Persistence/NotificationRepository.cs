using Common.Infrastructure.Tracing;
using Microsoft.EntityFrameworkCore;
using Notifications.Application.Records;

namespace Notifications.Infrastructure.Persistence;

internal sealed class NotificationRepository(NotificationsDbContext db) : INotificationRepository
{
    // The unique key's own columns, so the read is the index's seek (§7.2).
    public Task<bool> ExistsAsync(Guid eventId, string templateKey, CancellationToken ct) =>
        db.NotificationLog.AnyAsync(n => n.EventId == eventId && n.TemplateKey == templateKey, ct);

    public Task<Notification?> GetAsync(Guid notificationId, CancellationToken ct) =>
        db.NotificationLog.SingleOrDefaultAsync(n => n.NotificationId == notificationId, ct);

    // The intake's trace, kept on the row so the send worker's pass joins it (§9.4).
    public void Add(Notification notification) => StagedTraceColumns.Stamp(db.NotificationLog.Add(notification));
}
