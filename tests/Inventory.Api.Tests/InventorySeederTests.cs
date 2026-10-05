using System.Text.Json;
using Common.Application;
using Common.Contracts.Inventory.V1;
using Common.Infrastructure.Outbox;
using Common.TestSupport;
using Inventory.Domain.Stock;
using Inventory.Infrastructure.Persistence;
using Inventory.Migrator;
using Inventory.TestSupport;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Inventory.Api.Tests;

/// <summary><see cref="InventorySeeder"/> through the real job host, on databases the collection never reads.</summary>
[Collection(nameof(IntegrationCollection))]
public class InventorySeederTests(ServiceFixture fixture)
{
    private static readonly string[] Seeding = ["--environment=Development", "--Seed:Enabled=true"];

    [Fact]
    public async Task A_seeding_run_stocks_the_products_the_compose_readme_names()
    {
        string database = await MigrateAsync(Seeding);

        StockItem[] items = await StockAsync(database);
        IReadOnlyList<SeededProduct> expected = SeededProducts.FromComposeReadme();

        items
            .OrderBy(i => i.Id.Value)
            .Select(i => (i.Id.Value, i.Available, i.Reserved))
            .ShouldBe(expected.OrderBy(e => e.Id).Select(e => (e.Id, e.OnHand, 0)));
    }

    [Fact]
    public async Task Each_seeded_count_carries_the_StockLevelChanged_row_the_dispatcher_delivers()
    {
        string database = await MigrateAsync(Seeding);

        StockItem[] items = await StockAsync(database);
        OutboxMessage[] outbox = await OutboxAsync(database);

        outbox.Length.ShouldBe(items.Length);

        foreach (OutboxMessage row in outbox)
        {
            row.Lane.ShouldBe(OutboxLane.Broker);
            row.Attempts.ShouldBe(0);
            row.ProcessedAt.ShouldBeNull();

            // The dispatcher's own read of a row (§9.4): the map resolves the name, OutboxJson the payload.
            object payload = JsonSerializer.Deserialize(
                row.Payload,
                fixture.MessageTypes.Resolve(row.MessageType),
                fixture.OutboxJson.Options)!;
            StockLevelChanged changed = payload.ShouldBeOfType<StockLevelChanged>();
            StockItem item = items.Single(i => i.Id.Value == changed.ProductId);

            changed.MessageId.ShouldBe(row.MessageId);
            changed.CorrelationId.ShouldBe(item.Id.Value);
            row.CorrelationId.ShouldBe(item.Id.Value);
            changed.OccurredAt.ShouldBe(item.UpdatedAt);
            row.OccurredAt.ShouldBe(item.UpdatedAt);
            changed.QuantityAvailable.ShouldBe(item.Available);
        }
    }

    [Fact]
    public async Task A_second_seeding_run_changes_nothing()
    {
        string database = await MigrateAsync(Seeding);
        (Guid Id, string Version)[] stockBefore = await StockVersionsAsync(database);
        long[] outboxBefore = await OutboxIdsAsync(database);

        int exitCode = await RunAsync(database, Seeding);

        exitCode.ShouldBe(0);
        (await StockVersionsAsync(database)).ShouldBe(stockBefore, "a rowversion moves on any write to its row");
        (await OutboxIdsAsync(database)).ShouldBe(outboxBefore);
    }

    [Fact]
    public async Task A_seed_over_existing_stock_keeps_its_row_and_stages_nothing_for_it()
    {
        string database = await MigrateAsync([]);
        Guid taken = SeededProducts.FromComposeReadme()[0].Id;

        await using (InventoryDbContext db = Open(database))
        {
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO inventory.StockItems (ProductId, Available, Reserved, UpdatedAt)
                VALUES ({taken}, 7, 3, SYSDATETIMEOFFSET());
                """,
                TestContext.Current.CancellationToken);
        }

        int exitCode = await RunAsync(database, Seeding);

        exitCode.ShouldBe(0);
        StockItem[] items = await StockAsync(database);
        items.Length.ShouldBe(SeededProducts.FromComposeReadme().Count);
        StockItem kept = items.Single(i => i.Id.Value == taken);
        kept.Available.ShouldBe(7);
        kept.Reserved.ShouldBe(3);

        OutboxMessage[] outbox = await OutboxAsync(database);
        outbox.Length.ShouldBe(items.Length - 1);
        outbox.ShouldNotContain(row => row.CorrelationId == taken);
    }

    [Fact]
    public async Task A_run_without_the_flag_seeds_nothing()
    {
        string database = await MigrateAsync(["--environment=Development"]);

        (await StockAsync(database)).ShouldBeEmpty();
        (await OutboxAsync(database)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_seed_that_fails_after_the_schema_migrated_logs_the_seed_failure_and_not_a_migration_one()
    {
        string database = await MigrateAsync([]);

        // A database that does not exist, so the seed throws once the runner's own schema is current.
        string absent = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = $"InventorySeedAbsent{Guid.CreateVersion7():N}"
        }.ConnectionString;
        RunnerLog log = new();

        await using InventoryDbContext db = Open(database);
        await using InventoryDbContext elsewhere = Open(absent);
        MigrationRunner runner = new(db, log, new InventorySeeder(elsewhere, NullLogger<InventorySeeder>.Instance));

        int exitCode = await runner.RunAsync(TestContext.Current.CancellationToken);

        exitCode.ShouldBe(1);
        log.Errors.ShouldBe(["Inventory schema migrated, but the seed failed. The job exits non-zero."]);
    }

    /// <summary>A fresh database on the collection's server, migrated once with <paramref name="settings"/>.</summary>
    private async Task<string> MigrateAsync(string[] settings)
    {
        string database = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = $"InventorySeed{Guid.CreateVersion7():N}"
        }.ConnectionString;

        int exitCode = await RunAsync(database, settings);

        exitCode.ShouldBe(0);
        return database;
    }

    private static async Task<int> RunAsync(string database, string[] settings)
    {
        string[] args = [$"--ConnectionStrings:InventoryMigrator={database}", .. settings];

        using IHost host = MigratorHost.Build(args);
        using IServiceScope scope = host.Services.CreateScope();

        return await scope.ServiceProvider
            .GetRequiredService<MigrationRunner>()
            .RunAsync(TestContext.Current.CancellationToken);
    }

    private static InventoryDbContext Open(string database) =>
        new(new DbContextOptionsBuilder<InventoryDbContext>().UseSqlServer(database).Options);

    private static async Task<StockItem[]> StockAsync(string database)
    {
        await using InventoryDbContext db = Open(database);

        return await db.StockItems.AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<OutboxMessage[]> OutboxAsync(string database)
    {
        await using InventoryDbContext db = Open(database);

        return await db.OutboxMessages.AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<(Guid Id, string Version)[]> StockVersionsAsync(string database)
    {
        StockItem[] items = await StockAsync(database);

        return [.. items.OrderBy(i => i.Id.Value).Select(i => (i.Id.Value, Convert.ToHexString(i.Version)))];
    }

    private static async Task<long[]> OutboxIdsAsync(string database)
    {
        OutboxMessage[] outbox = await OutboxAsync(database);

        return [.. outbox.Select(row => row.Id).Order()];
    }

    /// <summary>The runner's <c>Error</c> sentences, the lines an operator reads first.</summary>
    private sealed class RunnerLog : ILogger<MigrationRunner>
    {
        public List<string> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error)
                Errors.Add(formatter(state, exception));
        }
    }
}
