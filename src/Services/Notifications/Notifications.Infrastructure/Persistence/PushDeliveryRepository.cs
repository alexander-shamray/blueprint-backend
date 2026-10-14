using Common.Infrastructure.Tracing;
using Microsoft.EntityFrameworkCore;
using Notifications.Application.Records;

namespace Notifications.Infrastructure.Persistence;

internal sealed class PushDeliveryRepository(NotificationsDbContext db) : IPushDeliveryRepository
{
    public Task<PushDelivery?> GetAsync(Guid deliveryId, CancellationToken ct) =>
        db.PushDeliveries.SingleOrDefaultAsync(d => d.DeliveryId == deliveryId, ct);

    // The fan-out's trace, kept on the row so the push send worker's pass joins it (§9.4).
    public void Add(PushDelivery delivery) => StagedTraceColumns.Stamp(db.PushDeliveries.Add(delivery));
}
