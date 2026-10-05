using System.Text.Json;
using Catalog.Domain.Products;
using Catalog.Infrastructure.Persistence;
using Catalog.Migrator;
using Catalog.TestSupport;
using Common.Application;
using Common.Contracts.Catalog.V1;
using Common.Infrastructure.Outbox;
using Common.TestSupport;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Catalog.Api.Tests;

/// <summary><see cref="CatalogSeeder"/> through the real job host, on databases the collection never reads.</summary>
[Collection(nameof(IntegrationCollection))]
public class CatalogSeederTests(ServiceFixture fixture)
{
    private static readonly string[] Seeding = ["--environment=Development", "--Seed:Enabled=true"];

    [Fact]
    public async Task A_seeding_run_publishes_the_products_the_compose_readme_names()
    {
        string database = await MigrateAsync(Seeding);

        Product[] products = await ProductsAsync(database);
        IReadOnlyList<SeededProduct> expected = SeededProducts.FromComposeReadme();

        products
            .OrderBy(p => p.Id.Value)
            .Select(p => (p.Id.Value, p.Name, p.Price.Amount, p.Price.Currency))
            .ShouldBe(expected.OrderBy(e => e.Id).Select(e => (e.Id, e.Name, e.Amount, e.Currency)));
    }

    [Fact]
    public async Task Each_seeded_product_carries_the_ProductPublished_row_the_dispatcher_delivers()
    {
        string database = await MigrateAsync(Seeding);

        Product[] products = await ProductsAsync(database);
        OutboxMessage[] outbox = await OutboxAsync(database);

        outbox.Length.ShouldBe(products.Length);

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
            ProductPublished published = payload.ShouldBeOfType<ProductPublished>();
            Product product = products.Single(p => p.Id.Value == published.ProductId);

            published.MessageId.ShouldBe(row.MessageId);
            published.CorrelationId.ShouldBe(product.Id.Value);
            row.CorrelationId.ShouldBe(product.Id.Value);
            published.OccurredAt.ShouldBe(product.PublishedAt);
            row.OccurredAt.ShouldBe(product.PublishedAt);
            published.Name.ShouldBe(product.Name);
            published.ThumbnailUrl.ShouldBeNull();
            published.Amount.ShouldBe(product.Price.Amount);
            published.Currency.ShouldBe(product.Price.Currency);
        }
    }

    [Fact]
    public async Task A_second_seeding_run_changes_nothing()
    {
        string database = await MigrateAsync(Seeding);
        (Guid Id, string Version)[] productsBefore = await ProductVersionsAsync(database);
        long[] outboxBefore = await OutboxIdsAsync(database);

        int exitCode = await RunAsync(database, Seeding);

        exitCode.ShouldBe(0);
        (await ProductVersionsAsync(database)).ShouldBe(productsBefore, "a rowversion moves on any write to its row");
        (await OutboxIdsAsync(database)).ShouldBe(outboxBefore);
    }

    [Fact]
    public async Task A_seed_over_an_existing_product_keeps_its_row_and_stages_nothing_for_it()
    {
        string database = await MigrateAsync([]);
        Guid taken = SeededProducts.FromComposeReadme()[0].Id;

        await using (CatalogDbContext db = Open(database))
        {
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO catalog.Products (Id, Name, ThumbnailUrl, PriceAmount, PriceCurrency, PublishedAt)
                VALUES ({taken}, N'Written by hand', NULL, 1, 'EUR', SYSDATETIMEOFFSET());
                """,
                TestContext.Current.CancellationToken);
        }

        int exitCode = await RunAsync(database, Seeding);

        exitCode.ShouldBe(0);
        Product[] products = await ProductsAsync(database);
        products.Length.ShouldBe(SeededProducts.FromComposeReadme().Count);
        products.Single(p => p.Id.Value == taken).Name.ShouldBe("Written by hand");

        OutboxMessage[] outbox = await OutboxAsync(database);
        outbox.Length.ShouldBe(products.Length - 1);
        outbox.ShouldNotContain(row => row.CorrelationId == taken);
    }

    [Fact]
    public async Task A_run_without_the_flag_seeds_nothing()
    {
        string database = await MigrateAsync(["--environment=Development"]);

        (await ProductsAsync(database)).ShouldBeEmpty();
        (await OutboxAsync(database)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_seed_that_fails_after_the_schema_migrated_logs_the_seed_failure_and_not_a_migration_one()
    {
        string database = await MigrateAsync([]);

        // A database that does not exist, so the seed throws once the runner's own schema is current.
        string absent = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = $"CatalogSeedAbsent{Guid.CreateVersion7():N}"
        }.ConnectionString;
        RunnerLog log = new();

        await using CatalogDbContext db = Open(database);
        await using CatalogDbContext elsewhere = Open(absent);
        MigrationRunner runner = new(db, log, new CatalogSeeder(elsewhere, NullLogger<CatalogSeeder>.Instance));

        int exitCode = await runner.RunAsync(TestContext.Current.CancellationToken);

        exitCode.ShouldBe(1);
        log.Errors.ShouldBe(["Catalog schema migrated, but the seed failed. The job exits non-zero."]);
    }

    /// <summary>A fresh database on the collection's server, migrated once with <paramref name="settings"/>.</summary>
    private async Task<string> MigrateAsync(string[] settings)
    {
        string database = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = $"CatalogSeed{Guid.CreateVersion7():N}"
        }.ConnectionString;

        int exitCode = await RunAsync(database, settings);

        exitCode.ShouldBe(0);
        return database;
    }

    private static async Task<int> RunAsync(string database, string[] settings)
    {
        string[] args = [$"--ConnectionStrings:CatalogMigrator={database}", .. settings];

        using IHost host = MigratorHost.Build(args);
        using IServiceScope scope = host.Services.CreateScope();

        return await scope.ServiceProvider
            .GetRequiredService<MigrationRunner>()
            .RunAsync(TestContext.Current.CancellationToken);
    }

    private static CatalogDbContext Open(string database) =>
        new(new DbContextOptionsBuilder<CatalogDbContext>().UseSqlServer(database).Options);

    private static async Task<Product[]> ProductsAsync(string database)
    {
        await using CatalogDbContext db = Open(database);

        return await db.Set<Product>().AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<OutboxMessage[]> OutboxAsync(string database)
    {
        await using CatalogDbContext db = Open(database);

        return await db.OutboxMessages.AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<(Guid Id, string Version)[]> ProductVersionsAsync(string database)
    {
        Product[] products = await ProductsAsync(database);

        return [.. products.OrderBy(p => p.Id.Value).Select(p => (p.Id.Value, Convert.ToHexString(p.Version)))];
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
