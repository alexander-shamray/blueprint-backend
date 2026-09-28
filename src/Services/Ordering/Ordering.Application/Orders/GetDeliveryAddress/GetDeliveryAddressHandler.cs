using System.Data;
using Common.Application;
using Dapper;

namespace Ordering.Application.Orders.GetDeliveryAddress;

/// <summary>
/// §6.5's read side over <c>ordering.Orders</c>: where one order ships, for
/// the worker ADR-052 gives the read to. No status, no total, no lines.
/// </summary>
/// <remarks>
/// No such order, a cancelled order and an order whose address erasure has
/// cleared all answer <c>null</c>, which ADR-052 makes the contract: the
/// client maps one status and never reads an order's state. A view that
/// distinguished them would put the distinction on the wire.
/// </remarks>
public sealed class GetDeliveryAddressHandler(IDbConnectionFactory connections)
    : IQueryHandler<GetDeliveryAddressQuery, DeliveryAddressView?>
{
    // Cancelled is compared as text because §7.2 persists the status by name.
    // The filter is in the statement rather than in the branch below so the
    // cancelled row never leaves the database.
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

        // The erasure case, designed against before the consumer that produces
        // it exists (ADR-052). §11.7's extension clears an erased subject's
        // address in place and leaves the order's own record whole, so the row
        // survives with nothing to ship to — and an answer of five blank
        // strings is a parcel addressed to nowhere rather than an absence.
        return string.IsNullOrWhiteSpace(address?.Line1) ? null : address;
    }
}
