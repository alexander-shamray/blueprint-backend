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
    // A shipment whose list holds the request was placed after it, so a reissue of that request leaves its order,
    // its list and its address whole, as Ordering's store leaves the order (ADR-140).
    private const string PlacedUnder =
        """
        EXISTS (
            SELECT 1
            FROM shipping.Shipments AS p
            CROSS APPLY OPENJSON(p.ErasedUnder) AS u
            WHERE p.OrderId = a.OrderId
                AND u.value = @RequestId)
        """;

    private const string AddressedOrdersSql =
        $"""
        SELECT a.OrderId
        FROM shipping.DeliveryAddresses AS a
        WHERE a.CustomerId = @SubjectId
            AND NOT {PlacedUnder};
        """;

    // Run first, while the address row still names the order's customer: the list names the buyer to whoever can
    // join it to an open request (ADR-103), and the shipment beside it stays whole. It opens with a locking read of
    // the subject's rows: the update holds no address lock to the commit, so a row saved between it and the delete
    // would be deleted with its list kept (ADR-146).
    internal const string EmptyErasedUnderSql =
        $"""
        DECLARE @held int = (
            SELECT COUNT(*) FROM shipping.DeliveryAddresses WITH (UPDLOCK, HOLDLOCK) WHERE CustomerId = @SubjectId);

        UPDATE s
        SET ErasedUnder = N'[]'
        FROM shipping.Shipments AS s
        INNER JOIN shipping.DeliveryAddresses AS a
            ON a.OrderId = s.OrderId
        WHERE a.CustomerId = @SubjectId
            AND NOT {PlacedUnder};
        """;

    // Deleted, not anonymised: the row is the address and nothing else, and the shipment beside it stays whole.
    // HOLDLOCK takes a key-range lock even where the predicate matches nothing, and keeps it to the commit that
    // writes the audit row: a worker saving an address meanwhile waits, and its erased check then sees that row.
    private const string DeleteAddressesSql =
        $"""
        DELETE a FROM shipping.DeliveryAddresses AS a WITH (UPDLOCK, HOLDLOCK)
        WHERE a.CustomerId = @SubjectId
            AND NOT {PlacedUnder};
        """;

    public async Task<IReadOnlyList<Guid>> AddressedOrdersAsync(Guid subjectId, Guid requestId, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        return
        [
            .. await connection.QueryAsync<Guid>(
                new CommandDefinition(AddressedOrdersSql, Under(subjectId, requestId), cancellationToken: ct))
        ];
    }

    public async Task<int> DeleteAddressesAsync(Guid subjectId, Guid requestId, CancellationToken ct)
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
                Under(subjectId, requestId),
                current.GetDbTransaction(),
                cancellationToken: ct));

        return await connection.ExecuteAsync(
            new CommandDefinition(
                DeleteAddressesSql,
                Under(subjectId, requestId),
                current.GetDbTransaction(),
                cancellationToken: ct));
    }

    // As the list's JSON spells a Guid, which is what it is matched against.
    private static object Under(Guid subjectId, Guid requestId) =>
        new { SubjectId = subjectId, RequestId = requestId.ToString() };
}
