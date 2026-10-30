using Microsoft.Data.SqlClient;
using Ordering.TestSupport;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>MassTransit's delivery DELETE, compiled where a scan is cheapest: over an empty table (ADR-167).</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class SagaOutboxPlanTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // Every index, so one ordering a row by when it was staged, the identity's or a time's, fails here (ADR-177).
    [Fact]
    public async Task The_table_is_keyed_and_clustered_on_the_inbox_message_index_and_no_index_is_in_staging_order()
    {
        (await fixture.ScalarAsync<string>(
            """
            SELECT Value = STRING_AGG(
                CONCAT(k.name, ':', k.type_desc, ':', k.is_primary_key, ':', k.columns) COLLATE DATABASE_DEFAULT, ' | ')
                WITHIN GROUP (ORDER BY k.name)
            FROM (
                SELECT
                    i.name,
                    i.type_desc,
                    i.is_primary_key,
                    columns = STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal)
                FROM sys.indexes i
                INNER JOIN sys.index_columns ic
                    ON ic.object_id = i.object_id
                    AND ic.index_id = i.index_id
                    AND ic.key_ordinal > 0
                INNER JOIN sys.columns c
                    ON c.object_id = ic.object_id
                    AND c.column_id = ic.column_id
                WHERE i.object_id = OBJECT_ID('ordering.OutboxMessage')
                GROUP BY i.name, i.type_desc, i.is_primary_key) k
            """)).ShouldBe(
            "IX_OutboxMessage_OutboxId_SequenceNumber:NONCLUSTERED:0:OutboxId,SequenceNumber | "
            + "PK_OutboxMessage:CLUSTERED:1:InboxMessageId,InboxConsumerId,SequenceNumber");

        // The delivery reads a message's rows in staging order, so the sequence keeps its identity.
        (await fixture.ScalarAsync<bool>(
            """
            SELECT Value = is_identity
            FROM sys.columns
            WHERE object_id = OBJECT_ID('ordering.OutboxMessage')
                AND name = 'SequenceNumber'
            """)).ShouldBeTrue();
    }

    [Fact]
    public async Task The_delivery_delete_seeks_one_message_s_rows_on_an_empty_table()
    {
        // Compiled and never run, on a connection of its own, so the instance's plan cache is not read or touched.
        await using SqlConnection connection = new(fixture.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using (SqlCommand showPlan = new("SET SHOWPLAN_XML ON;", connection))
            await showPlan.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        // The library's statement, its parameters declared, since SHOWPLAN does not compile inside sp_executesql.
        await using SqlCommand delete = new(
            """
            DECLARE @MessageId uniqueidentifier = NEWID(), @ConsumerId uniqueidentifier = NEWID();
            DELETE FROM [o]
            FROM [ordering].[OutboxMessage] AS [o]
            WHERE [o].[InboxMessageId] = @MessageId AND [o].[InboxConsumerId] = @ConsumerId;
            """,
            connection);

        // The batch's plan is one document today; reading every result set holds whichever shape comes back.
        List<string> plans = [];
        await using (SqlDataReader reader = await delete.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            do
            {
                while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                    plans.Add(reader.GetString(0));
            }
            while (await reader.NextResultAsync(TestContext.Current.CancellationToken));
        }

        string plan = plans.Where(p => p.Contains("OutboxMessage", StringComparison.Ordinal)).ShouldHaveSingleItem();

        plan.ShouldContain("PhysicalOp=\"Clustered Index Seek\"");
        plan.ShouldNotContain("Scan\"");
    }
}
