using Microsoft.EntityFrameworkCore;
using Notifications.Application.Records;

namespace Notifications.Infrastructure.Persistence;

internal sealed class OrderRecordRepository(NotificationsDbContext db) : IOrderRecordRepository
{
    public Task<OrderRecord?> GetAsync(Guid orderId, CancellationToken ct) =>
        db.OrderRecords.SingleOrDefaultAsync(r => r.OrderId == orderId, ct);

    // An intake that changes nothing on the record would otherwise read it shared, beside the erasure's update locks,
    // and stage a notice the erasure never deletes. No HOLDLOCK: it would hold a range lock on an absent order's next
    // key, which can be one of the subject's records, where a plain insert waits on that key and keeps nothing.
    public async Task<OrderRecord?> GetHeldAsync(Guid orderId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A record is held only inside the unit of work's transaction (§6.3).");

        await db.Database.ExecuteSqlAsync(
            $"SELECT OrderId FROM notifications.OrderRecords WITH (UPDLOCK) WHERE OrderId = {orderId};",
            ct);

        return await db.OrderRecords.SingleOrDefaultAsync(r => r.OrderId == orderId, ct);
    }

    public void Add(OrderRecord record) => db.OrderRecords.Add(record);
}
