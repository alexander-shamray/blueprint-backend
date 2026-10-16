using System.Data;
using Common.Application;
using Dapper;

namespace Ordering.Application.Orders.GetSagaState;

/// <summary>§9.6's saga instance for one order, read where the saga keeps it, for the staff who compare it.</summary>
/// <remarks>
/// No row is one answer for two facts: a finalised saga deletes its instance, and an order the saga never saw has
/// none (§9.6, <c>SetCompletedWhenFinalized</c>).
/// </remarks>
public sealed class GetSagaStateHandler(IDbConnectionFactory connections)
    : IQueryHandler<GetSagaStateQuery, SagaStateView?>
{
    private const string Sql =
        """
        SELECT s.OrderId,
               s.CurrentState,
               s.StartedAt,
               s.CancelReason
        FROM ordering.OrderFulfilmentStates s
        WHERE s.CorrelationId = @OrderId;
        """;

    public async Task<SagaStateView?> HandleAsync(GetSagaStateQuery query, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        return await connection.QuerySingleOrDefaultAsync<SagaStateView>(
            new CommandDefinition(Sql, new { query.OrderId }, cancellationToken: ct));
    }
}
