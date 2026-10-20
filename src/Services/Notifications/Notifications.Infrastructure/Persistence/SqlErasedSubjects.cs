using System.Data;
using Common.Application;
using Common.Domain;
using Dapper;
using Notifications.Application.Privacy;

namespace Notifications.Infrastructure.Persistence;

/// <summary>Answers from the audit rows alone: the hash is salted by its request, so each row is tried in
/// turn.</summary>
/// <remarks>On its own connection, after the intake's unit has committed. Unbounded by the order's age: the owner
/// anonymises on its own clock, so no age Notifications can compute is sound (ADR-092). A row is skipped only when
/// the order itself names its request as one it was placed after (ADR-099).</remarks>
internal sealed class SqlErasedSubjects(IDbConnectionFactory connections) : IErasedSubjects
{
    private const string ErasuresSql =
        """
        SELECT RequestId, SubjectHash
        FROM notifications.PersonalDataErasures;
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
