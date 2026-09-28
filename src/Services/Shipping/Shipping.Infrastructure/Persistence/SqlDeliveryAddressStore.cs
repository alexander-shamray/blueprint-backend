using System.Data;
using Common.Application;
using Dapper;
using Shipping.Application.Addresses;
using Shipping.Application.Carrier;
using Shipping.Domain.Shipments;

namespace Shipping.Infrastructure.Persistence;

/// <summary>
/// The only reader and writer of <c>shipping.DeliveryAddresses</c> (spec,
/// section 7).
/// </summary>
/// <remarks>
/// On its own connection, unlike <c>IUnitOfWork.ExecuteRawAsync</c>'s callers:
/// the worker writes this row before it calls the carrier (spec, section 4),
/// so there is no unit of work to enlist in — and joining one would hold a
/// transaction across a third party's latency.
/// </remarks>
internal sealed class SqlDeliveryAddressStore(IDbConnectionFactory connections) : IDeliveryAddressStore
{
    // No lock hints, because no two writers of one row ever overlap: the only
    // writer is the fulfilment worker, whose claim leases the shipment for
    // longer than a pass can run, and an order has one shipment (spec,
    // section 5). The pass that repeats after a crash reads this row before
    // it writes, so the update-then-insert shape is for a caller that saves
    // twice for one order: it ends with one row holding the later save rather
    // than a primary-key failure. Each statement autocommits on this
    // connection, so a hint would end with the update and span nothing.
    private const string SaveSql =
        """
        UPDATE shipping.DeliveryAddresses
        SET CustomerId = @CustomerId, Line1 = @Line1, Line2 = @Line2, City = @City,
            PostalCode = @PostalCode, Country = @Country, FetchedAt = @FetchedAt
        WHERE OrderId = @OrderId;

        IF @@ROWCOUNT = 0
            INSERT INTO shipping.DeliveryAddresses
                (OrderId, CustomerId, Line1, Line2, City, PostalCode, Country, FetchedAt)
            VALUES (@OrderId, @CustomerId, @Line1, @Line2, @City, @PostalCode, @Country, @FetchedAt);
        """;

    private const string GetSql =
        """
        SELECT Line1, Line2, City, PostalCode, Country
        FROM shipping.DeliveryAddresses
        WHERE OrderId = @OrderId;
        """;

    private sealed record Row(string Line1, string? Line2, string City, string PostalCode, string Country);

    public async Task SaveAsync(
        OrderId orderId,
        Guid customerId,
        DeliveryAddress address,
        DateTimeOffset fetchedAt,
        CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        await connection.ExecuteAsync(new CommandDefinition(
            SaveSql,
            new
            {
                OrderId = orderId.Value,
                CustomerId = customerId,
                address.Line1,
                address.Line2,
                address.City,
                address.PostalCode,
                address.Country,
                FetchedAt = fetchedAt
            },
            cancellationToken: ct));
    }

    public async Task<DeliveryAddress?> GetAsync(OrderId orderId, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        Row? row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            GetSql, new { OrderId = orderId.Value }, cancellationToken: ct));

        // char(2) space-pads a value shorter than its width; Country is
        // always exactly two letters by contract, so this never fires, but
        // the trim matches SqlPaymentOrderStore.LockAsync's char(3) currency
        // for the same reason.
        return row is null
            ? null
            : new DeliveryAddress(row.Line1, row.Line2, row.City, row.PostalCode, row.Country.Trim());
    }
}
