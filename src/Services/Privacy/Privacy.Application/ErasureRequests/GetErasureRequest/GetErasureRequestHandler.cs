using System.Data;
using Common.Application;
using Dapper;

namespace Privacy.Application.ErasureRequests.GetErasureRequest;

/// <summary>§6.5's read side over the write table, selecting the columns that name no subject.</summary>
public sealed class GetErasureRequestHandler(IDbConnectionFactory connections)
    : IQueryHandler<GetErasureRequestQuery, Result<ErasureRequestView>>
{
    private const string Sql =
        """
        SELECT RequestId, Status, RaisedAt, DueAt, Responders = RespondersCsv
        FROM privacy.ErasureRequests
        WHERE RequestId = @RequestId;
        """;

    private sealed record Row(
        Guid RequestId,
        string Status,
        DateTimeOffset RaisedAt,
        DateTimeOffset DueAt,
        string Responders);

    public async Task<Result<ErasureRequestView>> HandleAsync(GetErasureRequestQuery query, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        Row? row = await connection.QuerySingleOrDefaultAsync<Row>(
            new CommandDefinition(Sql, new { query.RequestId }, cancellationToken: ct));

        return row is null
            ? Result.Failure<ErasureRequestView>(ErasureRequestErrors.NotFound)
            : Result.Success(new ErasureRequestView(
                row.RequestId,
                row.Status,
                row.RaisedAt,
                row.DueAt,
                row.Responders.Split(',')));
    }
}
