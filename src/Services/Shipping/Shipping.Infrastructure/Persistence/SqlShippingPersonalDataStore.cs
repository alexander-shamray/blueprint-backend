using System.Data;
using System.Data.Common;
using Common.Application;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Shipping.Application.Privacy;

namespace Shipping.Infrastructure.Persistence;

internal sealed class SqlShippingPersonalDataStore(ShippingDbContext db, IDbConnectionFactory connections)
    : IShippingPersonalDataStore
{
    private const string AddressedOrdersSql =
        """
        SELECT OrderId
        FROM shipping.DeliveryAddresses
        WHERE CustomerId = @SubjectId;
        """;

    // Run first, while the address row still names the order's customer: the list names the buyer to whoever can
    // join it to an open request (ADR-103), and the shipment beside it stays whole.
    private const string EmptyErasedUnderSql =
        """
        UPDATE s
        SET ErasedUnder = N'[]'
        FROM shipping.Shipments AS s
        JOIN shipping.DeliveryAddresses AS a ON a.OrderId = s.OrderId
        WHERE a.CustomerId = @SubjectId;
        """;

    // Deleted, not anonymised: the row is the address and nothing else, and the shipment beside it stays whole.
    // HOLDLOCK takes a key-range lock even where the predicate matches nothing, and keeps it to the commit that
    // writes the audit row: a worker saving an address meanwhile waits, and its erased check then sees that row.
    private const string DeleteAddressesSql =
        """
        DELETE FROM shipping.DeliveryAddresses WITH (UPDLOCK, HOLDLOCK)
        WHERE CustomerId = @SubjectId;
        """;

    public async Task<IReadOnlyList<Guid>> AddressedOrdersAsync(Guid subjectId, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        return
        [
            .. await connection.QueryAsync<Guid>(
                new CommandDefinition(AddressedOrdersSql, new { SubjectId = subjectId }, cancellationToken: ct))
        ];
    }

    public async Task<int> DeleteAddressesAsync(Guid subjectId, CancellationToken ct)
    {
        IDbContextTransaction? current = db.Database.CurrentTransaction;

        // As in EfUnitOfWork.ExecuteRawAsync: with no transaction, the statement would autocommit outside the unit.
        if (current is null)
        {
            throw new InvalidOperationException(
                "A customer's data is erased only inside the unit of work's transaction (§6.3).");
        }

        DbConnection connection = db.Database.GetDbConnection();

        await connection.ExecuteAsync(
            new CommandDefinition(
                EmptyErasedUnderSql,
                new { SubjectId = subjectId },
                current.GetDbTransaction(),
                cancellationToken: ct));

        return await connection.ExecuteAsync(
            new CommandDefinition(
                DeleteAddressesSql,
                new { SubjectId = subjectId },
                current.GetDbTransaction(),
                cancellationToken: ct));
    }
}
