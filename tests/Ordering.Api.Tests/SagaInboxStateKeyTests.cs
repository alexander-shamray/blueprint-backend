using Ordering.TestSupport;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>MassTransit's inbox, clustered where a message's id sorts, not on its identity key (ADR-171).</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class SagaInboxStateKeyTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task The_table_is_clustered_on_the_message_key_and_the_identity_key_is_not()
    {
        (await fixture.ScalarAsync<string>(
            """
            SELECT Value = i.name + ':' + STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal)
            FROM sys.indexes i
            INNER JOIN sys.index_columns ic
                ON ic.object_id = i.object_id
                AND ic.index_id = i.index_id
                AND ic.key_ordinal > 0
            INNER JOIN sys.columns c
                ON c.object_id = ic.object_id
                AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID('ordering.InboxState')
                AND i.type = 1
            GROUP BY i.name
            """)).ShouldBe("AK_InboxState_MessageId_ConsumerId:MessageId,ConsumerId");

        (await fixture.ScalarAsync<string>(
            """
            SELECT Value = type_desc
            FROM sys.indexes
            WHERE object_id = OBJECT_ID('ordering.InboxState')
                AND is_primary_key = 1
            """)).ShouldBe("NONCLUSTERED");

        // The saga outbox's foreign key is dropped beneath the key and restored, so its loss would pass the two reads.
        (await fixture.ScalarAsync<string>(
            """
            SELECT Value = name
            FROM sys.foreign_keys
            WHERE referenced_object_id = OBJECT_ID('ordering.InboxState')
            """)).ShouldBe("FK_OutboxMessage_InboxState_InboxMessageId_InboxConsumerId");
    }
}
