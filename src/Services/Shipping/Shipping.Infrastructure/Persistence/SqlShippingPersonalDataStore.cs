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

    // Deleted, not anonymised: the row is the address and nothing else, and the shipment beside it stays whole.
    private const string DeleteAddressesSql =
        """
        DELETE FROM shipping.DeliveryAddresses
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

        return await connection.ExecuteAsync(
            new CommandDefinition(
                DeleteAddressesSql,
                new { SubjectId = subjectId },
                current.GetDbTransaction(),
                cancellationToken: ct));
    }
}
