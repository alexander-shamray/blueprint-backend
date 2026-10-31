using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Infrastructure.Messaging;
using Ordering.Infrastructure.Persistence;
using Ordering.TestSupport;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>MassTransit's inbox, removed a bucket at a time by this service, not the library (ADR-182).</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class SagaInboxCleanupTests(ServiceFixture fixture) : IAsyncLifetime
{
    // Far from the window on either side, so each test is about the predicate, not a boundary.
    private static DateTime LongAgo => DateTime.UtcNow.AddDays(-1);
    private static DateTime Recently => DateTime.UtcNow.AddMinutes(-1);

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // The library's index on Delivered alone put every delivery on its last page (ADR-182).
    [Fact]
    public async Task The_cleanup_index_leads_on_the_bucket_and_holds_only_delivered_rows()
    {
        (await fixture.ScalarAsync<string>(
            """
            SELECT Value = STRING_AGG(
                CONCAT(k.name, ':', k.columns, ':', k.filter_definition) COLLATE DATABASE_DEFAULT, ' | ')
                WITHIN GROUP (ORDER BY k.name)
            FROM (
                SELECT
                    i.name,
                    i.filter_definition,
                    columns = STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal)
                FROM sys.indexes i
                INNER JOIN sys.index_columns ic
                    ON ic.object_id = i.object_id
                    AND ic.index_id = i.index_id
                    AND ic.key_ordinal > 0
                INNER JOIN sys.columns c
                    ON c.object_id = ic.object_id
                    AND c.column_id = ic.column_id
                WHERE i.object_id = OBJECT_ID('ordering.InboxState')
                    AND i.type = 2
                    AND i.is_primary_key = 0
                GROUP BY i.name, i.filter_definition) k
            """)).ShouldBe("IX_InboxState_CleanupBucket_Delivered:CleanupBucket,Delivered:([Delivered] IS NOT NULL)");

        (await fixture.ScalarAsync<string>(
            """
            SELECT Value = CONCAT(is_persisted, ':', definition)
            FROM sys.computed_columns
            WHERE object_id = OBJECT_ID('ordering.InboxState')
                AND name = 'CleanupBucket'
            """)).ShouldBe("0:(CONVERT([tinyint],[Id]&(15)))");
    }

    [Fact]
    public async Task A_delivered_row_past_the_window_is_removed_from_every_bucket()
    {
        // Two ids a bucket, so a pass that read one bucket, or one row a bucket, leaves rows behind.
        long[] ids = [.. Enumerable.Range(0, 2 * OrderingDbContext.InboxStateCleanupBuckets).Select(i => (long)i)];
        foreach (long id in ids)
            await InsertAsync(id, delivered: LongAgo);

        (await Cleanup().CleanUpAsync(TestContext.Current.CancellationToken)).ShouldBe(ids.Length);

        (await CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task A_row_inside_the_window_or_never_delivered_survives()
    {
        // Undelivered: its outbox rows may not have reached the broker, so removing it loses them (§9.5).
        await InsertAsync(1, delivered: null, received: LongAgo);
        await InsertAsync(2, delivered: Recently);
        await InsertAsync(3, delivered: LongAgo);

        (await Cleanup().CleanUpAsync(TestContext.Current.CancellationToken)).ShouldBe(1);

        (await fixture.ScalarAsync<string>(
            """
            SELECT Value = STRING_AGG(CONVERT(varchar(20), Id), ',') WITHIN GROUP (ORDER BY Id)
            FROM ordering.InboxState
            """))
            .ShouldBe("1,2");
    }

    [Fact]
    public async Task The_cleanup_delete_seeks_its_bucket_s_expired_rows_rather_than_scanning()
    {
        // Compiled and never run, on a connection of its own, so the instance's plan cache is not read or touched.
        await using SqlConnection connection = new(fixture.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using (SqlCommand showPlan = new("SET SHOWPLAN_XML ON;", connection))
            await showPlan.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        // The service's statement, its parameters declared, since SHOWPLAN does not compile inside sp_executesql.
        await using SqlCommand delete = new(
            $"""
            DECLARE @Bucket tinyint = 3, @Before datetime2 = SYSUTCDATETIME();
            {SagaInboxCleanupService.CleanupSql}
            """,
            connection);

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

        string plan = plans.Where(p => p.Contains("InboxState", StringComparison.Ordinal)).ShouldHaveSingleItem();

        // A scan would walk past rows in flight, whose locks a delivery holds until it commits.
        plan.ShouldContain("Index=\"[IX_InboxState_CleanupBucket_Delivered]\"");
        plan.ShouldContain("PhysicalOp=\"Index Seek\"");
        plan.ShouldNotContain("Scan\"");
    }

    private SagaInboxCleanupService Cleanup() =>
        fixture.Factory.Services.GetRequiredService<SagaInboxCleanupService>();

    private Task InsertAsync(long id, DateTime? delivered, DateTime? received = null) =>
        fixture.ExecuteAsync(
            """
            INSERT INTO ordering.InboxState (Id, MessageId, ConsumerId, LockId, Received, ReceiveCount, Delivered)
            VALUES ({0}, NEWID(), NEWID(), NEWID(), {1}, 1, {2});
            """,
            id,
            received ?? delivered!.Value,
            new SqlParameter { SqlDbType = SqlDbType.DateTime2, Value = (object?)delivered ?? DBNull.Value });

    private Task<int> CountAsync() =>
        fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM ordering.InboxState");
}
