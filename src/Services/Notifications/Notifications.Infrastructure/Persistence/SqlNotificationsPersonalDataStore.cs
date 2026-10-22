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

    // An order whose record lists the request was placed under it, so it is not the request's to erase, nor are its
    // notices and their deliveries (ADR-099, ADR-138). Completed by the caller's correlation on r.OrderId.
    private const string PlacedUnder =
        """
        SELECT 1 FROM notifications.OrderRecords AS r CROSS APPLY OPENJSON(r.ErasedUnder) AS u
        WHERE u.value = @RequestId
        """;

    // A device row or a journal entry written under the request, which the subject's own act made after it.
    private const string WrittenUnder =
        "EXISTS (SELECT 1 FROM OPENJSON(ErasedUnder) AS u WHERE u.value = @RequestId)";

    // The erasure's first statement, so it reads the records before anything is deleted. HOLDLOCK takes a range lock
    // even where the subject has no record, held to the commit that writes the audit row, and the second read holds
    // the records themselves, which the index's seek does not: an intake meanwhile waits at its held read of the
    // customer's range, and its erased check then sees that row. Its own step, as a plan may skip a subquery, and
    // uncounted.
    private const string DeleteWaitingSql =
        $"""
        SET NOCOUNT ON;
        DECLARE @Orders TABLE (OrderId uniqueidentifier NOT NULL);

        INSERT INTO @Orders
        SELECT OrderId FROM notifications.OrderRecords WITH (UPDLOCK, HOLDLOCK) WHERE CustomerId = @SubjectId;

        DECLARE @Held int;
        SELECT @Held = COUNT(*)
        FROM notifications.OrderRecords WITH (UPDLOCK, HOLDLOCK, FORCESEEK)
        WHERE OrderId IN (SELECT OrderId FROM @Orders);

        SET NOCOUNT OFF;
        DELETE n
        FROM notifications.NotificationLog AS n
        WHERE n.Status = 'Pending'
            AND (n.CustomerId = @SubjectId OR n.OrderId IN (SELECT OrderId FROM @Orders))
            AND NOT EXISTS ({PlacedUnder} AND r.OrderId = n.OrderId);
        """;

    // The ended row keeps what shows a notice was sent (ADR-053 rule 4) and loses what names the customer: the id, the
    // language tag chosen from their contact, and the values that were merged into the message.
    private const string AnonymiseEndedSql =
        $"""
        UPDATE n
        SET CustomerId = NULL, Languages = NULL, Parameters = ''
        FROM notifications.NotificationLog AS n
        WHERE n.Status <> 'Pending'
            AND (n.CustomerId = @SubjectId OR n.OrderId IN ({SubjectsOrders}))
            AND NOT EXISTS ({PlacedUnder} AND r.OrderId = n.OrderId);
        """;

    // A delivery carries its customer from the fan-out, so it is found by it, and kept through its notice's order.
    private const string OfKeptOrder =
        $"""
        EXISTS (
            SELECT 1 FROM notifications.NotificationLog AS n
            WHERE n.NotificationId = d.NotificationId AND EXISTS ({PlacedUnder} AND r.OrderId = n.OrderId))
        """;

    private const string DeleteWaitingDeliveriesSql =
        $"""
        DELETE d
        FROM notifications.PushDeliveries AS d
        WHERE d.Status = 'Pending' AND d.CustomerId = @SubjectId AND NOT {OfKeptOrder};
        """;

    // As the ended notices: the row keeps what shows a push was sent, and loses whom to and in which language.
    private const string AnonymiseEndedDeliveriesSql =
        $"""
        UPDATE d
        SET CustomerId = NULL, Languages = NULL
        FROM notifications.PushDeliveries AS d
        WHERE d.Status <> 'Pending' AND d.CustomerId = @SubjectId AND NOT {OfKeptOrder};
        """;

    private const string DeleteOrderRecordsSql =
        """
        DELETE FROM notifications.OrderRecords
        WHERE CustomerId = @SubjectId
            AND NOT EXISTS (SELECT 1 FROM OPENJSON(ErasedUnder) AS u WHERE u.value = @RequestId);
        """;

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

    private const string DeleteWaitingOrderDeliveriesSql =
        """
        DELETE d
        FROM notifications.PushDeliveries AS d
        JOIN notifications.NotificationLog AS n ON n.NotificationId = d.NotificationId
        WHERE d.Status = 'Pending' AND n.OrderId = @OrderId;
        """;

    private const string AnonymiseEndedOrderDeliveriesSql =
        """
        UPDATE d
        SET CustomerId = NULL, Languages = NULL
        FROM notifications.PushDeliveries AS d
        JOIN notifications.NotificationLog AS n ON n.NotificationId = d.NotificationId
        WHERE d.Status <> 'Pending' AND n.OrderId = @OrderId;
        """;

    private const string DeleteOrderRecordSql =
        "DELETE FROM notifications.OrderRecords WHERE OrderId = @OrderId AND CustomerId = @SubjectId;";

    // Guarded by the subject, so a registration that lands after the erasure is a new row of the subject's own act,
    // and the kept sequence refuses a retry of one made before it (ADR-117, ADR-118). One written under the request
    // is such a row, so a reissue keeps it (ADR-138).
    private const string ClearDevicesSql =
        $"""
        UPDATE notifications.DeviceInstallations WITH (UPDLOCK, HOLDLOCK)
        SET SubjectId = NULL, Platform = NULL, AppId = NULL, Token = NULL, RegisteredAt = NULL, ErasedUnder = N'[]'
        WHERE SubjectId = @SubjectId AND NOT {WrittenUnder};
        """;

    // Every entry keeps its pair, value, times, client and chain as evidence, and loses whom it was about. Over the
    // subject's range, held, so an append runs wholly before or after it (ADR-120); kept as the devices are.
    private const string AnonymiseConsentSql =
        $"""
        UPDATE notifications.ConsentEntries WITH (UPDLOCK, HOLDLOCK)
        SET SubjectId = NULL, AnonymisedAt = @Now, ErasedUnder = N'[]'
        WHERE SubjectId = @SubjectId AND NOT {WrittenUnder};
        """;

    public Task<int> DeleteWaitingNoticesAsync(Guid subjectId, Guid requestId, CancellationToken ct) =>
        ExecuteAsync(DeleteWaitingSql, Under(subjectId, requestId), ct);

    public Task<int> AnonymiseEndedNoticesAsync(Guid subjectId, Guid requestId, CancellationToken ct) =>
        ExecuteAsync(AnonymiseEndedSql, Under(subjectId, requestId), ct);

    public Task<int> DeleteWaitingDeliveriesAsync(Guid subjectId, Guid requestId, CancellationToken ct) =>
        ExecuteAsync(DeleteWaitingDeliveriesSql, Under(subjectId, requestId), ct);

    public Task<int> AnonymiseEndedDeliveriesAsync(Guid subjectId, Guid requestId, CancellationToken ct) =>
        ExecuteAsync(AnonymiseEndedDeliveriesSql, Under(subjectId, requestId), ct);

    public Task<int> DeleteOrderRecordsAsync(Guid subjectId, Guid requestId, CancellationToken ct) =>
        ExecuteAsync(DeleteOrderRecordsSql, Under(subjectId, requestId), ct);

    public Task<int> DeleteContactAsync(Guid subjectId, CancellationToken ct) =>
        ExecuteAsync(DeleteContactSql, subjectId, ct);

    public Task<int> DeleteWaitingOrderNoticesAsync(Guid orderId, CancellationToken ct) =>
        ExecuteAsync(DeleteWaitingOrderSql, new { OrderId = orderId }, ct);

    public Task<int> AnonymiseEndedOrderNoticesAsync(Guid orderId, CancellationToken ct) =>
        ExecuteAsync(AnonymiseEndedOrderSql, new { OrderId = orderId }, ct);

    public Task<int> DeleteWaitingOrderDeliveriesAsync(Guid orderId, CancellationToken ct) =>
        ExecuteAsync(DeleteWaitingOrderDeliveriesSql, new { OrderId = orderId }, ct);

    public Task<int> AnonymiseEndedOrderDeliveriesAsync(Guid orderId, CancellationToken ct) =>
        ExecuteAsync(AnonymiseEndedOrderDeliveriesSql, new { OrderId = orderId }, ct);

    public Task<int> DeleteOrderRecordAsync(Guid subjectId, Guid orderId, CancellationToken ct) =>
        ExecuteAsync(DeleteOrderRecordSql, new { SubjectId = subjectId, OrderId = orderId }, ct);

    // Counted at the statement, inside the unit's transaction: a rolled-back erasure is retried and counts again,
    // which over-reads a series whose job is to show that removals happen (the push design's section 9).
    public async Task<int> ClearDevicesAsync(Guid subjectId, Guid requestId, CancellationToken ct)
    {
        int cleared = await ExecuteAsync(ClearDevicesSql, Under(subjectId, requestId), ct);
        metrics.DevicesRemoved(PushMetrics.Erased, cleared);

        return cleared;
    }

    public Task<int> AnonymiseConsentAsync(Guid subjectId, Guid requestId, DateTimeOffset now, CancellationToken ct) =>
        ExecuteAsync(
            AnonymiseConsentSql,
            new { SubjectId = subjectId, RequestId = requestId.ToString(), Now = now },
            ct);

    // The request as text, the form OPENJSON returns a list's values in.
    private static object Under(Guid subjectId, Guid requestId) =>
        new { SubjectId = subjectId, RequestId = requestId.ToString() };

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
