using System.Data.Common;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Notifications.Application.Privacy;

namespace Notifications.Infrastructure.Persistence;

internal sealed class SqlNotificationsPersonalDataStore(NotificationsDbContext db) : INotificationsPersonalDataStore
{
    // A notice names its customer only once the worker has resolved it, so a waiting one is found through its order.
    private const string SubjectsOrders =
        "SELECT OrderId FROM notifications.OrderRecords WHERE CustomerId = @SubjectId";

    private const string DeleteWaitingSql =
        $"""
        DELETE FROM notifications.NotificationLog
        WHERE Status = 'Pending'
            AND (CustomerId = @SubjectId OR OrderId IN ({SubjectsOrders}));
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

    private const string DeleteOrderRecordsSql =
        "DELETE FROM notifications.OrderRecords WHERE CustomerId = @SubjectId;";

    private const string DeleteContactSql =
        "DELETE FROM notifications.ContactRecords WHERE CustomerId = @SubjectId;";

    public Task<int> DeleteWaitingNoticesAsync(Guid subjectId, CancellationToken ct) =>
        ExecuteAsync(DeleteWaitingSql, subjectId, ct);

    public Task<int> AnonymiseEndedNoticesAsync(Guid subjectId, CancellationToken ct) =>
        ExecuteAsync(AnonymiseEndedSql, subjectId, ct);

    public Task<int> DeleteOrderRecordsAsync(Guid subjectId, CancellationToken ct) =>
        ExecuteAsync(DeleteOrderRecordsSql, subjectId, ct);

    public Task<int> DeleteContactAsync(Guid subjectId, CancellationToken ct) =>
        ExecuteAsync(DeleteContactSql, subjectId, ct);

    private async Task<int> ExecuteAsync(string sql, Guid subjectId, CancellationToken ct)
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
                new { SubjectId = subjectId },
                current.GetDbTransaction(),
                cancellationToken: ct));
    }
}
