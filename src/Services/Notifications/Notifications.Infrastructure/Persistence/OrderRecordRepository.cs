using Microsoft.EntityFrameworkCore;
using Notifications.Application.Records;

namespace Notifications.Infrastructure.Persistence;

internal sealed class OrderRecordRepository(NotificationsDbContext db) : IOrderRecordRepository
{
    public Task<OrderRecord?> GetAsync(Guid orderId, CancellationToken ct) =>
        db.OrderRecords.SingleOrDefaultAsync(r => r.OrderId == orderId, ct);

    // The customer's range first and then the record, the order the erasure's first statement takes them in, so
    // either side waits holding nothing: an intake that took its notice before meeting the range at its record's
    // insert would deadlock with the erasure's later scans, and one that read the record shared would not wait.
    public async Task<OrderRecord?> GetHeldAsync(Guid orderId, Guid customerId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A record is held only inside the unit of work's transaction (§6.3).");

        await db.Database.ExecuteSqlAsync(
            $"""
            SELECT OrderId FROM notifications.OrderRecords WITH (UPDLOCK, HOLDLOCK, FORCESEEK)
            WHERE CustomerId = {customerId};
            SELECT OrderId FROM notifications.OrderRecords WITH (UPDLOCK) WHERE OrderId = {orderId};
            """,
            ct);

        return await db.OrderRecords.SingleOrDefaultAsync(r => r.OrderId == orderId, ct);
    }

    public void Add(OrderRecord record) => db.OrderRecords.Add(record);
}
