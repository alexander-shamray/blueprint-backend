using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Notifications.Application.Records;

namespace Notifications.Infrastructure.Persistence;

internal sealed class OrderRecordRepository(NotificationsDbContext db) : IOrderRecordRepository
{
    public Task<OrderRecord?> GetAsync(Guid orderId, CancellationToken ct) =>
        db.OrderRecords.SingleOrDefaultAsync(r => r.OrderId == orderId, ct);

    private const string HeldSql =
        """
        SET NOCOUNT ON;
        DECLARE @Found int = (SELECT COUNT(*) FROM notifications.OrderRecords WITH (UPDLOCK) WHERE OrderId = @OrderId);
        DECLARE @Range int;
        IF @Found = 0
            SELECT @Range = COUNT(*) FROM notifications.OrderRecords WITH (UPDLOCK, HOLDLOCK, FORCESEEK)
            WHERE CustomerId = @CustomerId;
        SELECT @Found;
        """;

    // The record's key under an update lock. Only an absent record takes the customer's range as well, the erasure's
    // first lock, so its intake waits there before writing anything; meeting the range at the record's insert, after
    // staging a notice, deadlocked with the erasure's later scans. A deleter of another of the customer's records may
    // wait on the range holding its key, no cycle, as the intake needs nothing it holds; a record that appears
    // meanwhile is unheld, so it is refused for the retry to read held.
    public async Task<OrderRecord?> GetHeldAsync(Guid orderId, Guid customerId, CancellationToken ct)
    {
        IDbContextTransaction transaction = db.Database.CurrentTransaction ?? throw new InvalidOperationException(
            "A record is held only inside the unit of work's transaction (§6.3).");

        int found = await db.Database.GetDbConnection().QuerySingleAsync<int>(
            new CommandDefinition(
                HeldSql,
                new { OrderId = orderId, CustomerId = customerId },
                transaction.GetDbTransaction(),
                cancellationToken: ct));

        OrderRecord? record = await db.OrderRecords.SingleOrDefaultAsync(r => r.OrderId == orderId, ct);

        if (found == 0 && record is not null)
        {
            throw new InvalidOperationException(
                "The order's record was written while this intake waited for its customer; the retry reads it held.");
        }

        return record;
    }

    public void Add(OrderRecord record) => db.OrderRecords.Add(record);
}
