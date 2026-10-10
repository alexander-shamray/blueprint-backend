using System.Data.Common;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ordering.Application.Privacy;

namespace Ordering.Infrastructure.Persistence;

internal sealed class SqlOrderPersonalDataStore(OrderingDbContext db) : IOrderPersonalDataStore
{
    // The empty id names nobody; the address columns are required, so they are emptied and not nulled, and the
    // country keeps the two-letter shape (ZZ constructs, as Address allows). The order and its money stay.
    private const string AnonymiseOrdersSql =
        """
        UPDATE ordering.Orders
        SET CustomerId = '00000000-0000-0000-0000-000000000000',
            ShipToLine1 = '', ShipToLine2 = NULL, ShipToCity = '', ShipToPostalCode = '', ShipToCountry = 'ZZ'
        WHERE CustomerId = @SubjectId;
        """;

    private const string AnonymiseSummariesSql =
        """
        UPDATE ordering.OrderSummaries
        SET CustomerId = NULL
        WHERE CustomerId = @SubjectId;
        """;

    public Task<int> AnonymiseOrdersAsync(Guid subjectId, CancellationToken ct) =>
        ExecuteAsync(AnonymiseOrdersSql, subjectId, ct);

    public Task<int> AnonymiseSummariesAsync(Guid subjectId, CancellationToken ct) =>
        ExecuteAsync(AnonymiseSummariesSql, subjectId, ct);

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
