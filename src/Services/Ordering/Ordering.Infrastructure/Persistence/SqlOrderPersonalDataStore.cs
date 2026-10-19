using System.Data.Common;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ordering.Application.Privacy;

namespace Ordering.Infrastructure.Persistence;

internal sealed class SqlOrderPersonalDataStore(OrderingDbContext db) : IOrderPersonalDataStore
{
    // The empty id names nobody; the address columns are required, so they are emptied and not nulled, and the
    // country keeps the two-letter shape (ZZ constructs, as Address allows). The order and its money stay. The
    // requests it was placed under go with the id, since a request id joined to an open request names the buyer
    // (ADR-095, ADR-099). An order whose list holds the request was placed after it, so a reissue leaves it whole.
    private const string AnonymiseOrdersSql =
        """
        UPDATE ordering.Orders
        SET CustomerId = '00000000-0000-0000-0000-000000000000',
            ShipToLine1 = '', ShipToLine2 = NULL, ShipToCity = '', ShipToPostalCode = '', ShipToCountry = 'ZZ',
            ErasedUnder = N'[]'
        WHERE CustomerId = @SubjectId
            AND NOT EXISTS (SELECT 1 FROM OPENJSON(ErasedUnder) AS u WHERE u.value = @RequestId);
        """;

    // Run after the orders, so the order the request erased has lost its list and the one it spared still has it.
    private const string AnonymiseSummariesSql =
        """
        UPDATE s
        SET CustomerId = NULL
        FROM ordering.OrderSummaries AS s
        WHERE s.CustomerId = @SubjectId
            AND NOT EXISTS (
                SELECT 1
                FROM ordering.Orders AS o
                CROSS APPLY OPENJSON(o.ErasedUnder) AS u
                WHERE o.Id = s.OrderId AND u.value = @RequestId);
        """;

    public Task<int> AnonymiseOrdersAsync(Guid subjectId, Guid requestId, CancellationToken ct) =>
        ExecuteAsync(AnonymiseOrdersSql, subjectId, requestId, ct);

    public Task<int> AnonymiseSummariesAsync(Guid subjectId, Guid requestId, CancellationToken ct) =>
        ExecuteAsync(AnonymiseSummariesSql, subjectId, requestId, ct);

    private async Task<int> ExecuteAsync(string sql, Guid subjectId, Guid requestId, CancellationToken ct)
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
                new { SubjectId = subjectId, RequestId = requestId.ToString() },
                current.GetDbTransaction(),
                cancellationToken: ct));
    }
}
