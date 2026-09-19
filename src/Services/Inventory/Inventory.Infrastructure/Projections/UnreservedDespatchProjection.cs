using Common.Application;
using Dapper;
using Inventory.Application.Reservations;
using Inventory.Domain.Reservations.Events;
using System.Data;

namespace Inventory.Infrastructure.Projections;

/// <summary>§13.3's claim: flip and read in one statement, count only when it flipped.</summary>
public sealed class UnreservedDespatchProjection(IDbConnectionFactory connections, InventoryMetrics metrics)
    : IProjectionHandler<DespatchedUnreservedDomainEvent>
{
    private const string ClaimSql =
        """
        UPDATE inventory.Reservations
        SET UnreservedCounted = 1
        WHERE OrderId = @OrderId
            AND DespatchedUnreservedAt IS NOT NULL
            AND UnreservedCounted = 0;
        """;

    public async Task HandleAsync(DespatchedUnreservedDomainEvent domainEvent, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();
        int affected = await connection.ExecuteAsync(new CommandDefinition(
            ClaimSql, new { OrderId = domainEvent.OrderId.Value }, cancellationToken: ct));

        if (affected == 1)
            metrics.UnreservedDespatch();
    }
}
