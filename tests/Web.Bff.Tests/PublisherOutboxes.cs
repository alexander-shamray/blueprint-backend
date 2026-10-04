using BffReplay;
using Common.Application;
using Common.Contracts;
using Common.Infrastructure.Outbox;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Web.Bff.Tests;

/// <summary>Four publishers' outbox tables in one database of the fixture's server, staged as each writes.</summary>
/// <remarks>
/// The DDL is every service's <c>OutboxMessageConfiguration</c> in SQL (§9.4); the rows go through
/// <see cref="OutboxMessage.Stage"/>, so their payloads are the publishers' own bytes.
/// </remarks>
internal sealed class PublisherOutboxes(string serverConnectionString)
{
    private const string Database = "ReplayPublishers";

    private static readonly MessageTypeMap Contracts = new([typeof(IIntegrationEvent).Assembly]);

    private static readonly OutboxJson Json = new([]);

    public string ConnectionString { get; } =
        new SqlConnectionStringBuilder(serverConnectionString) { InitialCatalog = Database }.ConnectionString;

    /// <summary>Creates the database and its four tables once, and empties them on every call.</summary>
    public async Task ResetAsync()
    {
        SqlConnectionStringBuilder master = new(serverConnectionString) { InitialCatalog = "master" };
        await using (SqlConnection server = new(master.ConnectionString))
        {
            await server.ExecuteAsync($"IF DB_ID(N'{Database}') IS NULL CREATE DATABASE [{Database}];");
        }

        await using SqlConnection connection = new(ConnectionString);
        foreach (Publisher publisher in Publisher.All)
        {
            await connection.ExecuteAsync(
                $"""
                IF SCHEMA_ID(N'{publisher.Schema}') IS NULL EXEC (N'CREATE SCHEMA [{publisher.Schema}]');
                IF OBJECT_ID(N'{publisher.Schema}.OutboxMessages') IS NULL
                    CREATE TABLE {publisher.Outbox}
                    (
                        Id            bigint IDENTITY  NOT NULL PRIMARY KEY,
                        MessageId     uniqueidentifier NOT NULL,
                        CorrelationId uniqueidentifier NOT NULL,
                        MessageType   nvarchar(300)    NOT NULL,
                        Payload       nvarchar(max)    NOT NULL,
                        Lane          varchar(16)      NOT NULL,
                        OccurredAt    datetimeoffset   NOT NULL,
                        ProcessedAt   datetimeoffset   NULL,
                        Attempts      int              NOT NULL,
                        LastError     nvarchar(2000)   NULL,
                        LockedUntil   datetimeoffset   NULL
                    );
                TRUNCATE TABLE {publisher.Outbox};
                """);
        }
    }

    /// <summary>Stages events in one publisher's outbox, processed at the instant given or not at all.</summary>
    public async Task StageAsync(Publisher publisher, DateTimeOffset? processedAt, params IIntegrationEvent[] events)
    {
        await using SqlConnection connection = new(ConnectionString);
        foreach (IIntegrationEvent message in events)
        {
            OutboxMessage row = OutboxMessage.Stage(message, OutboxLane.Broker, message.CorrelationId, Contracts, Json);

            await connection.ExecuteAsync(
                $"""
                INSERT INTO {publisher.Outbox}
                    (MessageId, CorrelationId, MessageType, Payload, Lane, OccurredAt, ProcessedAt, Attempts)
                VALUES (@MessageId, @CorrelationId, @MessageType, @Payload, @Lane, @OccurredAt, @ProcessedAt, 0);
                """,
                new
                {
                    row.MessageId,
                    row.CorrelationId,
                    row.MessageType,
                    row.Payload,
                    Lane = row.Lane.ToString(),
                    row.OccurredAt,
                    ProcessedAt = processedAt
                });
        }
    }

    /// <summary>Rewrites every payload in one publisher's outbox to JSON that is no event at all.</summary>
    public async Task CorruptPayloadsAsync(Publisher publisher)
    {
        await using SqlConnection connection = new(ConnectionString);
        await connection.ExecuteAsync($"UPDATE {publisher.Outbox} SET Payload = N'[]';");
    }
}
