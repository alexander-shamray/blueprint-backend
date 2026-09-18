using System.Data;
using Common.Application;
using Dapper;

namespace Inventory.Application.Reservations.GetReservation;

public sealed class GetReservationHandler(IDbConnectionFactory connections)
    : IQueryHandler<GetReservationQuery, ReservationDto?>
{
    private const string Sql =
        """
        SELECT OrderId, Status, UpdatedAt FROM inventory.Reservations WHERE OrderId = @OrderId;
        SELECT ProductId, Quantity FROM inventory.ReservationLines WHERE OrderId = @OrderId ORDER BY ProductId;
        """;

    public async Task<ReservationDto?> HandleAsync(GetReservationQuery query, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();
        using SqlMapper.GridReader grid = await connection.QueryMultipleAsync(
            new CommandDefinition(Sql, new { query.OrderId }, cancellationToken: ct));

        // A named record, not a ValueTuple: Dapper maps columns by name, and a
        // tuple's members are Item1..Item3.
        ReservationHead? head = await grid.ReadSingleOrDefaultAsync<ReservationHead>();
        if (head is null)
            return null;

        List<ReservationLineDto> lines = (await grid.ReadAsync<ReservationLineDto>()).AsList();
        return new ReservationDto(head.OrderId, head.Status, lines, head.UpdatedAt);
    }

    private sealed record ReservationHead(Guid OrderId, string Status, DateTimeOffset UpdatedAt);
}
