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

        IReadOnlyList<Guid> naming = await RequestsNamingAsync(connection, transaction: null, subjectId, ct);

        return naming.Any(r => !placedAfter.Contains(r));
    }

    /// <summary>The requests whose audit row names the subject; the saved address reads them in its own unit.</summary>
    internal static async Task<IReadOnlyList<Guid>> RequestsNamingAsync(
        IDbConnection connection,
        IDbTransaction? transaction,
        Guid subjectId,
        CancellationToken ct)
    {
        IEnumerable<Row> rows = await connection.QueryAsync<Row>(
            new CommandDefinition(ErasuresSql, transaction: transaction, cancellationToken: ct));

        return
        [
            .. rows
                .Where(r => PersonalDataErasure.HashSubject(r.RequestId, subjectId) == r.SubjectHash.Trim())
                .Select(r => r.RequestId)
        ];
    }
}
