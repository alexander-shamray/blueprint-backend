using System.Data;
using Common.Application;
using Common.Domain;
using Dapper;
using Shipping.Application.Privacy;

namespace Shipping.Infrastructure.Persistence;

/// <summary>Answers from the audit rows alone: the hash is salted by its request, so each row is tried in
/// turn.</summary>
/// <remarks>On its own connection: the worker asks after it saves, outside any unit (ADR-052). Bounded by the
/// request's close and never by the holder's own row, which the owner's clock does not follow (ADR-092, ADR-139). A
/// row is skipped too when the shipment names its request as one the order was placed under (ADR-099,
/// ADR-103).</remarks>
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
