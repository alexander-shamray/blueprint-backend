using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>The indexes the send claim and the retention pass seek on, read from the engine.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class SendIndexesSchemaTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Each index's table, name, first key column and filter, as SQL Server normalises a filter.</summary>
    public static TheoryData<string, string, string, string?> Indexes() => new()
    {
        { "NotificationLog", "IX_NotificationLog_SendClaim", "NextAttemptAt", "([Status]='Pending')" },
        { "NotificationLog", "IX_NotificationLog_PendingOrder", "OrderId", "([Status]='Pending')" },
        { "NotificationLog", "IX_NotificationLog_CompletedAt", "CompletedAt", "([CompletedAt] IS NOT NULL)" },
        { "ContactRecords", "IX_ContactRecords_FetchedAt", "FetchedAt", null },
        { "OrderRecords", "IX_OrderRecords_RecordedAt", "RecordedAt", null },
    };

    [Theory]
    [MemberData(nameof(Indexes))]
    public async Task Each_index_keys_its_column_under_its_filter(
        string table,
        string index,
        string column,
        string? filter)
    {
        string qualified = $"notifications.{table}";

        (await fixture.ScalarAsync<string>(
            """
            SELECT Value = c.name
            FROM sys.indexes i
            INNER JOIN sys.index_columns ic
                ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 1
            INNER JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID({0}) AND i.name = {1}
            """,
            qualified,
            index)).ShouldBe(column);

        (await fixture.ScalarAsync<string?>(
            "SELECT Value = filter_definition FROM sys.indexes WHERE object_id = OBJECT_ID({0}) AND name = {1}",
            qualified,
            index)).ShouldBe(filter);
    }

    [Fact]
    public async Task The_claim_s_index_carries_the_lease_so_the_claim_reads_no_row_it_skips()
    {
        (await fixture.ScalarAsync<int>(
            """
            SELECT Value = COUNT(*)
            FROM sys.indexes i
            INNER JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            INNER JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.name = 'IX_NotificationLog_SendClaim' AND ic.is_included_column = 1 AND c.name = 'LockedUntil'
            """)).ShouldBe(1);
    }
}
