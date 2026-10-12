using System.Data;
using Common.Application;
using Common.Domain;
using Dapper;
using Shipping.Application.Privacy;

namespace Shipping.Infrastructure.Persistence;

/// <summary>Answers from the audit rows alone: the hash is salted by its request, so each row is tried in turn.</summary>
/// <remarks>On its own connection: the worker asks after it saves, outside any unit (ADR-052).
/// Unbounded by the shipment's age: the owner anonymises on its own clock (ADR-092). A row is skipped only when the
/// shipment itself names its request as one the order was placed under (ADR-099, ADR-103).</remarks>
internal sealed class SqlErasedSubjects(IDbConnectionFactory connections) : IErasedSubjects
{
    private const string ErasuresSql =
        """
        SELECT RequestId, SubjectHash
        FROM shipping.PersonalDataErasures;
        """;

    private sealed record Row(Guid RequestId, string SubjectHash);

    public async Task<bool> IsErasedAsync(Guid subjectId, IReadOnlyCollection<Guid> placedAfter, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        IEnumerable<Row> rows = await connection.QueryAsync<Row>(
            new CommandDefinition(ErasuresSql, cancellationToken: ct));

        return rows.Any(r =>
            !placedAfter.Contains(r.RequestId) &&
            PersonalDataErasure.HashSubject(r.RequestId, subjectId) == r.SubjectHash.Trim());
    }
}
