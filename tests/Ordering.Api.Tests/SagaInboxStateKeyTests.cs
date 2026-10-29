using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Ordering.Infrastructure.Persistence;
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

    // ADR-171's rebuild drops PK_InboxState before it adds it back, a table the running hosts must never see.
    [Fact]
    public void No_migration_runs_a_statement_outside_the_migrations_transaction()
    {
        using OrderingDbContext db = new(
            new DbContextOptionsBuilder<OrderingDbContext>().UseSqlServer(fixture.ConnectionString).Options);

        IMigrationsAssembly migrations = db.GetService<IMigrationsAssembly>();
        IMigrationsSqlGenerator generator = db.GetService<IMigrationsSqlGenerator>();
        IModelRuntimeInitializer initializer = db.GetService<IModelRuntimeInitializer>();
        string provider = db.Database.ProviderName!;

        // Named, so a run that read no migration fails rather than passing on an empty list.
        migrations.Migrations.Keys.ShouldContain(
            id => id.EndsWith("_ClusterInboxStateOnMessageKey", StringComparison.Ordinal));

        foreach ((string id, TypeInfo type) in migrations.Migrations)
        {
            Migration migration = migrations.CreateMigration(type, provider);

            // Against the finalised target model, as Database.Migrate generates it, so a suppression the
            // generator derives from the model is seen as well as one an operation carries.
            IModel? target = migration.TargetModel switch
            {
                null => null,
                IMutableModel building => initializer.Initialize(building.FinalizeModel(), designTime: true),
                IModel built => initializer.Initialize(built, designTime: true),
            };

            foreach ((string direction, IReadOnlyList<MigrationOperation> operations) in
                     new[] { ("Up", migration.UpOperations), ("Down", migration.DownOperations) })
            {
                generator.Generate(operations, target)
                    .Where(command => command.TransactionSuppressed)
                    .Select(command => command.CommandText)
                    .ShouldBeEmpty($"{id} {direction}");
            }
        }
    }
}
