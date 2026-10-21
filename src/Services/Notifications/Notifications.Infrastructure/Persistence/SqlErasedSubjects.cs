using System.Data;
using Common.Application;
using Common.Domain;
using Dapper;
using Notifications.Application.Privacy;

namespace Notifications.Infrastructure.Persistence;

/// <summary>Answers from the audit rows alone: the hash is salted by its request, so each row is tried in
/// turn.</summary>
/// <remarks>On its own connection, after the intake's unit has committed. Bounded by the request's close and never
/// by the order's age, since the owner anonymises on its own clock (ADR-092, ADR-139). A row is skipped too when
/// the order names its request as one it was placed after (ADR-099).</remarks>
internal sealed class SqlErasedSubjects(IDbConnectionFactory connections) : IErasedSubjects
{
    private const string ErasuresSql =
        """
        SELECT RequestId, SubjectHash
        FROM notifications.PersonalDataErasures
        WHERE ClosedAt IS NULL OR ClosedAt > @Since;
        """;

    private sealed record Row(Guid RequestId, string SubjectHash);

    public async Task<bool> IsErasedAsync(
        Guid subjectId,
        IReadOnlyCollection<Guid> placedAfter,
        DateTimeOffset sourcedAt,
        CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        IEnumerable<Row> rows = await connection.QueryAsync<Row>(
            new CommandDefinition(
                ErasuresSql,
                new { Since = PersonalDataErasure.AskedSince(sourcedAt) },
                cancellationToken: ct));

        return rows.Any(r =>
            !placedAfter.Contains(r.RequestId) &&
            PersonalDataErasure.HashSubject(r.RequestId, subjectId) == r.SubjectHash.Trim());
    }
}
