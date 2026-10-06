using System.Data;
using Common.Application;
using Dapper;
using Shipping.Application.Addresses;
using Shipping.Application.Carrier;
using Shipping.Domain.Shipments;

namespace Shipping.Infrastructure.Persistence;

/// <summary>The only reader and writer of whole addresses; retention and §11.7's erasure only delete rows.</summary>
/// <remarks>On its own connection: the worker saves before calling the carrier, outside any unit (ADR-052).</remarks>
internal sealed class SqlDeliveryAddressStore(IDbConnectionFactory connections) : IDeliveryAddressStore
{
    // No lock hints: the one writer holds the shipment's lease, and a hint would end with its autocommitted update.
    // Update-then-insert, so a second save for one order keeps the later one rather than failing on the key.
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

        await connection.ExecuteAsync(
            new CommandDefinition(
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

        Row? row = await connection.QuerySingleOrDefaultAsync<Row>(
            new CommandDefinition(GetSql, new { OrderId = orderId.Value }, cancellationToken: ct));

        // char(2) space-pads a shorter value; two letters by contract, so the trim is defensive.
        return row is null
            ? null
            : new DeliveryAddress(row.Line1, row.Line2, row.City, row.PostalCode, row.Country.Trim());
    }
}
