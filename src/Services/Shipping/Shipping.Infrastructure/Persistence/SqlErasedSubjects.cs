using System.Data;
using Common.Application;
using Common.Domain;
using Dapper;
using Shipping.Application.Privacy;

namespace Shipping.Infrastructure.Persistence;

/// <summary>Answers from the audit rows alone: the hash is salted by its request, so each row is tried in
/// turn.</summary>
/// <remarks>On its own connection, outside any unit (ADR-052). Bounded by the request's close, never by the holder's
/// own row (ADR-092, ADR-139), and a row the shipment lists as placed under is skipped (ADR-099, ADR-103).</remarks>
internal sealed class SqlErasedSubjects(IDbConnectionFactory connections) : IErasedSubjects
{
    private const string ErasuresSql =
        """
        SELECT RequestId, SubjectHash
        FROM shipping.PersonalDataErasures
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
