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

    [Fact]
    public async Task The_table_is_clustered_on_the_inbox_message_index_and_the_identity_key_is_not()
    {
        (await fixture.ScalarAsync<string>(
            """
            SELECT Value = STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal)
            FROM sys.indexes i
            INNER JOIN sys.index_columns ic
                ON ic.object_id = i.object_id
                AND ic.index_id = i.index_id
                AND ic.key_ordinal > 0
            INNER JOIN sys.columns c
                ON c.object_id = ic.object_id
                AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID('ordering.OutboxMessage')
                AND i.type = 1
            """)).ShouldBe("InboxMessageId,InboxConsumerId,SequenceNumber");

        (await fixture.ScalarAsync<string>(
            """
            SELECT Value = type_desc
            FROM sys.indexes
            WHERE object_id = OBJECT_ID('ordering.OutboxMessage')
                AND is_primary_key = 1
            """)).ShouldBe("NONCLUSTERED");
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

        // Each statement's plan comes back as a result set of its own.
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
