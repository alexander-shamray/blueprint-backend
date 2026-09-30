using System.Data;
using Common.Application;
using Dapper;

namespace Ordering.Application.Orders.GetDeliveryAddress;

/// <summary>§6.5's read of where one order ships, for the worker ADR-052 gives the read to.</summary>
/// <remarks>No order, a cancelled one and an erased address all answer <c>null</c>, as ADR-052 decides.</remarks>
public sealed class GetDeliveryAddressHandler(IDbConnectionFactory connections)
    : IQueryHandler<GetDeliveryAddressQuery, DeliveryAddressView?>
{
    // Compared as text, since §7.2 persists the status by name.
    private const string Sql =
        """
        SELECT o.CustomerId,
               o.ShipToLine1 AS Line1,
               o.ShipToLine2 AS Line2,
               o.ShipToCity AS City,
               o.ShipToPostalCode AS PostalCode,
               o.ShipToCountry AS Country
        FROM ordering.Orders o
        WHERE o.Id = @OrderId
            AND o.Status <> 'Cancelled';
        """;

    public async Task<DeliveryAddressView?> HandleAsync(GetDeliveryAddressQuery query, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        DeliveryAddressView? address = await connection.QuerySingleOrDefaultAsync<DeliveryAddressView>(
            new CommandDefinition(Sql, new { query.OrderId }, cancellationToken: ct));

        // An erased address leaves the row with nothing to ship to, which is an absence (§11.7, ADR-052).
        return string.IsNullOrWhiteSpace(address?.Line1) ? null : address;
    }
}
