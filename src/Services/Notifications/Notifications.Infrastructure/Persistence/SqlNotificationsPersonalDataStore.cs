using System.Data.Common;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Notifications.Application.Privacy;
using Notifications.Infrastructure.Push;

namespace Notifications.Infrastructure.Persistence;

internal sealed class SqlNotificationsPersonalDataStore(NotificationsDbContext db, PushMetrics metrics)
    : INotificationsPersonalDataStore
{
    // A notice names its customer only once the worker has resolved it, so a waiting one is found through its order.
    private const string SubjectsOrders =
        "SELECT OrderId FROM notifications.OrderRecords WHERE CustomerId = @SubjectId";

    // The erasure's first statement, so it reads the records before anything is deleted. HOLDLOCK takes a range lock
    // even where the subject has no record, held to the commit that writes the audit row: an intake meanwhile
    // waits, and its erased check then sees that row. The read is its own step because a plan may skip a subquery
    // when no notice is there to compare, and uncounted, as the count is of what the erasure removed.
    private const string DeleteWaitingSql =
        """
        SET NOCOUNT ON;
        DECLARE @Orders TABLE (OrderId uniqueidentifier NOT NULL);

        INSERT INTO @Orders
        SELECT OrderId FROM notifications.OrderRecords WITH (UPDLOCK, HOLDLOCK) WHERE CustomerId = @SubjectId;

        SET NOCOUNT OFF;
        DELETE FROM notifications.NotificationLog
        WHERE Status = 'Pending'
            AND (CustomerId = @SubjectId OR OrderId IN (SELECT OrderId FROM @Orders));
        """;

    // The ended row keeps what shows a notice was sent (ADR-053 rule 4) and loses what names the customer: the id, the
    // language tag chosen from their contact, and the values that were merged into the message.
    private const string AnonymiseEndedSql =
        $"""
        UPDATE notifications.NotificationLog
        SET CustomerId = NULL, Languages = NULL, Parameters = ''
        WHERE Status <> 'Pending'
            AND (CustomerId = @SubjectId OR OrderId IN ({SubjectsOrders}));
        """;

    // A delivery carries its customer from the fan-out, so it is found by it and never through its notice.
    private const string DeleteWaitingDeliveriesSql =
        "DELETE FROM notifications.PushDeliveries WHERE Status = 'Pending' AND CustomerId = @SubjectId;";

    // As the ended notices: the row keeps what shows a push was sent, and loses whom to and in which language.
    private const string AnonymiseEndedDeliveriesSql =
        """
        UPDATE notifications.PushDeliveries
        SET CustomerId = NULL, Languages = NULL
        WHERE Status <> 'Pending' AND CustomerId = @SubjectId;
        """;

    private const string DeleteOrderRecordsSql =
        "DELETE FROM notifications.OrderRecords WHERE CustomerId = @SubjectId;";

    // Held for the same reason: a contact read saving meanwhile waits for the audit row and then finds it.
    private const string DeleteContactSql =
        "DELETE FROM notifications.ContactRecords WITH (UPDLOCK, HOLDLOCK) WHERE CustomerId = @SubjectId;";

    // The late event's own order: the notice is deleted or anonymised through the order id, and the record only
    // while it is still the subject's.
    private const string DeleteWaitingOrderSql =
        "DELETE FROM notifications.NotificationLog WHERE Status = 'Pending' AND OrderId = @OrderId;";

    private const string AnonymiseEndedOrderSql =
        """
        UPDATE notifications.NotificationLog
        SET CustomerId = NULL, Languages = NULL, Parameters = ''
        WHERE Status <> 'Pending' AND OrderId = @OrderId;
        """;

    private const string DeleteOrderRecordSql =
        "DELETE FROM notifications.OrderRecords WHERE OrderId = @OrderId AND CustomerId = @SubjectId;";

    // Guarded by the subject, so a registration that lands after the erasure is a new row of the subject's own act,
    // and the kept sequence refuses a retry of one made before it (ADR-117, ADR-118).
    private const string ClearDevicesSql =
        """
        UPDATE notifications.DeviceInstallations WITH (UPDLOCK, HOLDLOCK)
        SET SubjectId = NULL, Platform = NULL, AppId = NULL, Token = NULL, RegisteredAt = NULL
        WHERE SubjectId = @SubjectId;
        """;

    // Every entry keeps its pair, value, times, client and chain as evidence, and loses whom it was about. Over the
    // subject's range, held, so an append runs wholly before or after it (ADR-120).
    private const string AnonymiseConsentSql =
        """
        UPDATE notifications.ConsentEntries WITH (UPDLOCK, HOLDLOCK)
        SET SubjectId = NULL, AnonymisedAt = @Now
        WHERE SubjectId = @SubjectId;
        """;

    public Task<int> DeleteWaitingNoticesAsync(Guid subjectId, CancellationToken ct) =>
        ExecuteAsync(DeleteWaitingSql, subjectId, ct);

    public Task<int> AnonymiseEndedNoticesAsync(Guid subjectId, CancellationToken ct) =>
        ExecuteAsync(AnonymiseEndedSql, subjectId, ct);

    public Task<int> DeleteWaitingDeliveriesAsync(Guid subjectId, CancellationToken ct) =>
        ExecuteAsync(DeleteWaitingDeliveriesSql, subjectId, ct);

    public Task<int> AnonymiseEndedDeliveriesAsync(Guid subjectId, CancellationToken ct) =>
        ExecuteAsync(AnonymiseEndedDeliveriesSql, subjectId, ct);

    public Task<int> DeleteOrderRecordsAsync(Guid subjectId, CancellationToken ct) =>
        ExecuteAsync(DeleteOrderRecordsSql, subjectId, ct);

    public Task<int> DeleteContactAsync(Guid subjectId, CancellationToken ct) =>
        ExecuteAsync(DeleteContactSql, subjectId, ct);

    public Task<int> DeleteWaitingOrderNoticesAsync(Guid orderId, CancellationToken ct) =>
        ExecuteAsync(DeleteWaitingOrderSql, new { OrderId = orderId }, ct);

    public Task<int> AnonymiseEndedOrderNoticesAsync(Guid orderId, CancellationToken ct) =>
        ExecuteAsync(AnonymiseEndedOrderSql, new { OrderId = orderId }, ct);

    public Task<int> DeleteOrderRecordAsync(Guid subjectId, Guid orderId, CancellationToken ct) =>
        ExecuteAsync(DeleteOrderRecordSql, new { SubjectId = subjectId, OrderId = orderId }, ct);

    // Counted at the statement, inside the unit's transaction: a rolled-back erasure is retried and counts again,
    // which over-reads a series whose job is to show that removals happen (the push design's section 9).
    public async Task<int> ClearDevicesAsync(Guid subjectId, CancellationToken ct)
    {
        int cleared = await ExecuteAsync(ClearDevicesSql, subjectId, ct);
        metrics.DevicesRemoved(PushMetrics.Erased, cleared);

        return cleared;
    }

    public Task<int> AnonymiseConsentAsync(Guid subjectId, DateTimeOffset now, CancellationToken ct) =>
        ExecuteAsync(AnonymiseConsentSql, new { SubjectId = subjectId, Now = now }, ct);

    private Task<int> ExecuteAsync(string sql, Guid subjectId, CancellationToken ct) =>
        ExecuteAsync(sql, new { SubjectId = subjectId }, ct);

    private async Task<int> ExecuteAsync(string sql, object parameters, CancellationToken ct)
    {
        IDbContextTransaction? current = db.Database.CurrentTransaction;

        // As in EfUnitOfWork.ExecuteRawAsync: with no transaction, the statement would autocommit outside the unit.
        if (current is null)
        {
            throw new InvalidOperationException(
                "A customer's data is erased only inside the unit of work's transaction (§6.3).");
        }

        DbConnection connection = db.Database.GetDbConnection();

        return await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                parameters,
                current.GetDbTransaction(),
                cancellationToken: ct));
    }
}
