using Common.Application;
using Common.Infrastructure.Outbox;
using Dapper;
using Microsoft.Data.SqlClient;

namespace BffReplay;

/// <summary>One processed outbox row, the six columns a replay needs of it.</summary>
public sealed record OutboxRow(
    long Id,
    Guid MessageId,
    Guid CorrelationId,
    string MessageType,
    string Payload,
    DateTimeOffset OccurredAt);

/// <summary>Step four of the rebuild: a publisher's processed Broker-lane rows of its share of the eight.</summary>
public static class OutboxRows
{
    // Named from the row type, so a renamed column fails the build rather than reading nothing.
    private static readonly string Columns = string.Join(
        ", ",
        nameof(OutboxMessage.Id),
        nameof(OutboxMessage.MessageId),
        nameof(OutboxMessage.CorrelationId),
        nameof(OutboxMessage.MessageType),
        nameof(OutboxMessage.Payload),
        nameof(OutboxMessage.OccurredAt));

    /// <summary>Reads no row, and fails on a missing table, column or grant: the preflight's question.</summary>
    public static string ProbeSql(Publisher publisher) =>
        $"SELECT TOP (0) {Columns}, {nameof(OutboxMessage.ProcessedAt)}, {nameof(OutboxMessage.Lane)} " +
        $"FROM {publisher.Outbox};";

    /// <summary>The publisher's own clock, which stamps <c>ProcessedAt</c> (§9.4), read when the run begins.</summary>
    public const string CutoffSql = "SELECT SYSDATETIMEOFFSET();";

    /// <summary>Rows processed by the cutoff; any other row is live traffic, which the dispatcher delivers.</summary>
    public static string ReadSql(Publisher publisher) =>
        $"""
        SELECT {Columns}
        FROM {publisher.Outbox}
        WHERE {nameof(OutboxMessage.ProcessedAt)} <= @Cutoff
            AND {nameof(OutboxMessage.Lane)} = @Lane
            AND {nameof(OutboxMessage.MessageType)} IN @Names
        ORDER BY {nameof(OutboxMessage.OccurredAt)}, {nameof(OutboxMessage.Id)};
        """;

    /// <summary>Unbuffered, since a window is whatever the publisher kept, not this process's to hold.</summary>
    public static IAsyncEnumerable<OutboxRow> ReadAsync(
        SqlConnection connection,
        Publisher publisher,
        IReadOnlyList<string> names,
        DateTimeOffset cutoff) =>
        connection.QueryUnbufferedAsync<OutboxRow>(
            ReadSql(publisher),
            new { Lane = nameof(OutboxLane.Broker), Names = names, Cutoff = cutoff });
}
