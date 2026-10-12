using System.Data;
using Common.Application;
using Common.Domain;
using Dapper;

namespace Web.Bff.Privacy;

/// <summary>Answers from the audit rows alone: the hash is salted by its request, so each row is tried in turn.</summary>
/// <remarks>On its own connection, after the projection's write has committed. Unbounded by the order's age: the
/// owner anonymises on its own clock, so no age the BFF can compute is sound (ADR-092). A row is skipped only when
/// the order itself names its request as one it was placed after (ADR-099).</remarks>
internal sealed class SqlErasedSubjects(IDbConnectionFactory connections) : IErasedSubjects
{
    private const string ErasuresSql =
        """
        SELECT RequestId, SubjectHash
        FROM bff.PersonalDataErasures;
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
