using System.Data;
using Common.Application;
using Dapper;

namespace Privacy.Application.ErasureRequests.GetErasureRequest;

/// <summary>§6.5's read side over the write tables, selecting the columns that name no subject.</summary>
public sealed class GetErasureRequestHandler(IDbConnectionFactory connections)
    : IQueryHandler<GetErasureRequestQuery, Result<ErasureRequestView>>
{
    private const string Sql =
        """
        SELECT RequestId, Status, RaisedAt, DueAt, ClosedAt, Reissues, Responders = RespondersCsv
        FROM privacy.ErasureRequests
        WHERE RequestId = @RequestId;

        SELECT Responder, Count, Counted, ReceivedAt
        FROM privacy.ErasureCompletions
        WHERE RequestId = @RequestId
        ORDER BY ReceivedAt, Responder;
        """;

    private sealed record Row(
        Guid RequestId,
        string Status,
        DateTimeOffset RaisedAt,
        DateTimeOffset DueAt,
        DateTimeOffset? ClosedAt,
        int Reissues,
        string Responders);

    public async Task<Result<ErasureRequestView>> HandleAsync(GetErasureRequestQuery query, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        using SqlMapper.GridReader grid = await connection.QueryMultipleAsync(
            new CommandDefinition(Sql, new { query.RequestId }, cancellationToken: ct));

        Row? row = await grid.ReadSingleOrDefaultAsync<Row>();
        if (row is null)
            return Result.Failure<ErasureRequestView>(ErasureRequestErrors.NotFound);

        HolderAnswer[] answers = [.. await grid.ReadAsync<HolderAnswer>()];
        string[] responders = row.Responders.Split(',');

        // The holders the request was raised with that have no counted answer, in the order it was raised with.
        string[] missing =
        [
            .. responders.Where(r => !answers.Any(a => a.Counted && a.Responder == r))
        ];

        return Result.Success(
            new ErasureRequestView(
                row.RequestId,
                row.Status,
                row.RaisedAt,
                row.DueAt,
                row.ClosedAt,
                row.Reissues,
                responders,
                missing,
                answers));
    }
}
