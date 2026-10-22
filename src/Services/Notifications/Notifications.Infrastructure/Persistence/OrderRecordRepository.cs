using Microsoft.EntityFrameworkCore;
using Notifications.Application.Records;

namespace Notifications.Infrastructure.Persistence;

internal sealed class OrderRecordRepository(NotificationsDbContext db) : IOrderRecordRepository
{
    public Task<OrderRecord?> GetAsync(Guid orderId, CancellationToken ct) =>
        db.OrderRecords.SingleOrDefaultAsync(r => r.OrderId == orderId, ct);

    // The record's key under an update lock. Only an absent record takes the customer's range as well, the erasure's
    // first lock, so its intake waits there before writing anything: meeting the range at the record's insert, after
    // staging a notice, deadlocked with the erasure's later scans. A deleter that locks a record's key before its
    // index entry reaches no absent record, so the range is never held between the two.
    public async Task<OrderRecord?> GetHeldAsync(Guid orderId, Guid customerId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A record is held only inside the unit of work's transaction (§6.3).");

        await db.Database.ExecuteSqlAsync(
            $"""
            DECLARE @Found int =
                (SELECT COUNT(*) FROM notifications.OrderRecords WITH (UPDLOCK) WHERE OrderId = {orderId});
            IF @Found = 0
                SELECT @Found = COUNT(*) FROM notifications.OrderRecords WITH (UPDLOCK, HOLDLOCK, FORCESEEK)
                WHERE CustomerId = {customerId};
            """,
            ct);

        return await db.OrderRecords.SingleOrDefaultAsync(r => r.OrderId == orderId, ct);
    }

    public void Add(OrderRecord record) => db.OrderRecords.Add(record);
}
