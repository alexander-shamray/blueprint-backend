using System.Text.Json;
using Catalog.Infrastructure.Persistence;
using Catalog.Migrator;
using Catalog.TestSupport;
using Common.Application;
using Common.Contracts;
using Common.Contracts.Catalog.V1;
using Common.Infrastructure.Outbox;
using Common.TestSupport;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace Catalog.Api.Tests;

/// <summary><see cref="CatalogRepublisher"/> through the real job host, on databases the collection never reads.</summary>
[Collection(nameof(IntegrationCollection))]
public class CatalogRepublisherTests(ServiceFixture fixture)
{
    private static readonly DateTimeOffset Published = new(2026, 3, 1, 9, 30, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Repriced = new(2026, 4, 2, 10, 15, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Withdrawn = new(2026, 5, 3, 11, 45, 0, TimeSpan.Zero);

    private static readonly Guid Untouched = Guid.Parse("a0000000-0000-0000-0000-000000000001");

    private static readonly Guid PriceMoved = Guid.Parse("a0000000-0000-0000-0000-000000000002");

    private static readonly Guid Pulled = Guid.Parse("a0000000-0000-0000-0000-000000000003");

    [Fact]
    public async Task Each_product_is_announced_with_the_stamps_its_own_events_carried()
    {
        string database = await CatalogueAsync();

        DateTimeOffset before = DateTimeOffset.UtcNow;
        (await RunAsync(database)).ShouldBe(0);

        IReadOnlyList<(OutboxMessage Row, object Event)> rows = await StagedAsync(database);

        rows.Count.ShouldBe(5, "three publications, one price change and one withdrawal");

        ProductPublished[] published = [.. rows.Select(r => r.Event).OfType<ProductPublished>()];
        published.Single(e => e.ProductId == Untouched).OccurredAt.ShouldBe(Published);
        published.Single(e => e.ProductId == PriceMoved).OccurredAt.ShouldBe(Published);
        published.Single(e => e.ProductId == Pulled).OccurredAt.ShouldBe(Published);

        PriceChanged change = rows.Select(r => r.Event).OfType<PriceChanged>().Single();
        change.ProductId.ShouldBe(PriceMoved);
        change.OccurredAt.ShouldBe(Repriced);
        change.Amount.ShouldBe(12.5m);
        change.Currency.ShouldBe("EUR");

        ProductDiscontinued gone = rows.Select(r => r.Event).OfType<ProductDiscontinued>().Single();
        gone.ProductId.ShouldBe(Pulled);
        gone.OccurredAt.ShouldBe(Withdrawn);

        // The row is the run's own, so §13.7's age measures this backlog and the dispatcher's order is the staging's.
        foreach ((OutboxMessage row, object @event) in rows)
        {
            row.Lane.ShouldBe(OutboxLane.Broker);
            row.Attempts.ShouldBe(0);
            row.ProcessedAt.ShouldBeNull();
            row.OccurredAt.ShouldBeGreaterThanOrEqualTo(before);
            ((IIntegrationEvent)@event).MessageId.ShouldBe(row.MessageId);
            ((IIntegrationEvent)@event).CorrelationId.ShouldBe(row.CorrelationId);
        }
    }

    [Fact]
    public async Task A_withdrawn_product_is_announced_as_published_and_then_withdrawn_and_not_as_repriced()
    {
        string database = await CatalogueAsync();

        (await RunAsync(database, Pulled)).ShouldBe(0);

        IReadOnlyList<(OutboxMessage Row, object Event)> rows = await StagedAsync(database);

        rows.Select(r => r.Event.GetType().Name)
            .Order()
            .ShouldBe([nameof(ProductDiscontinued), nameof(ProductPublished)]);
    }

    [Fact]
    public async Task A_second_run_stages_the_catalogue_again_under_new_message_ids()
    {
        string database = await CatalogueAsync();

        (await RunAsync(database)).ShouldBe(0);
        (await RunAsync(database)).ShouldBe(0);

        IReadOnlyList<(OutboxMessage Row, object Event)> rows = await StagedAsync(database);

        rows.Count.ShouldBe(10);
        rows.Select(r => r.Row.MessageId).Distinct().Count().ShouldBe(10);
    }

    [Fact]
    public async Task One_product_named_stages_its_own_rows_and_no_other()
    {
        string database = await CatalogueAsync();

        (await RunAsync(database, PriceMoved)).ShouldBe(0);

        IReadOnlyList<(OutboxMessage Row, object Event)> rows = await StagedAsync(database);

        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(r => r.Row.CorrelationId == PriceMoved);
    }

    [Fact]
    public async Task A_product_that_does_not_exist_is_a_failed_run_that_stages_nothing()
    {
        string database = await CatalogueAsync();

        (await RunAsync(database, Guid.Parse("a0000000-0000-0000-0000-0000000000ff"))).ShouldBe(1);

        (await StagedAsync(database)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_empty_catalogue_is_a_run_with_nothing_to_stage()
    {
        string database = await MigrateAsync();

        (await RunAsync(database)).ShouldBe(0);

        (await StagedAsync(database)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_run_that_cannot_reach_the_database_fails_and_says_so()
    {
        string absent = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = $"CatalogRepublishAbsent{Guid.CreateVersion7():N}"
        }.ConnectionString;

        (await RunAsync(absent)).ShouldBe(1);
    }

    /// <summary>A migrated database holding one untouched product, one repriced and one withdrawn.</summary>
    private async Task<string> CatalogueAsync()
    {
        string database = await MigrateAsync();

        await using CatalogDbContext db = Open(database);

        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO catalog.Products
                (Id, Name, ThumbnailUrl, PriceAmount, PriceCurrency, PublishedAt, LastEventAt, WithdrawnAt)
            VALUES
                ({Untouched}, N'Untouched', NULL, 10.00, 'EUR', {Published}, {Published}, NULL),
                ({PriceMoved}, N'Price moved', N'https://img.example/p.png', 12.50, 'EUR', {Published}, {Repriced}, NULL),
                ({Pulled}, N'Pulled', NULL, 9.00, 'EUR', {Published}, {Withdrawn}, {Withdrawn});
            """,
            TestContext.Current.CancellationToken);

        return database;
    }

    private async Task<string> MigrateAsync()
    {
        string database = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = $"CatalogRepublish{Guid.CreateVersion7():N}"
        }.ConnectionString;

        using IHost host = MigratorHost.Build(Args(database));
        using IServiceScope scope = host.Services.CreateScope();

        MigrationRunner runner = scope.ServiceProvider.GetRequiredService<MigrationRunner>();

        (await runner.RunAsync(TestContext.Current.CancellationToken)).ShouldBe(0);

        return database;
    }

    private static async Task<int> RunAsync(string database, Guid? only = null)
    {
        string[] named = only is { } id ? [$"--Republish:Id={id}"] : [];

        string[] args =
        [
            $"--ConnectionStrings:Catalog={database}",
            .. MigratorRun.PlaintextTestContainers("Catalog"),
            "--Republish:Enabled=true",
            .. named
        ];

        using IHost host = MigratorHost.Build(args);
        using IServiceScope scope = host.Services.CreateScope();

        return await scope.ServiceProvider
            .GetRequiredService<CatalogRepublisher>()
            .RunAsync(TestContext.Current.CancellationToken);
    }

    private static string[] Args(string database) =>
    [
        $"--ConnectionStrings:CatalogMigrator={database}",
        .. MigratorRun.PlaintextTestContainers("Catalog")
    ];

    private static CatalogDbContext Open(string database) =>
        new(new DbContextOptionsBuilder<CatalogDbContext>().UseSqlServer(database).Options);

    /// <summary>Each staged row with its payload read as the dispatcher reads it (§9.4).</summary>
    private async Task<IReadOnlyList<(OutboxMessage Row, object Event)>> StagedAsync(string database)
    {
        await using CatalogDbContext db = Open(database);

        OutboxMessage[] rows = await db.OutboxMessages.AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken);

        return [.. rows.Select(row => (row, Read(row)))];
    }

    private object Read(OutboxMessage row) =>
        JsonSerializer.Deserialize(row.Payload, fixture.MessageTypes.Resolve(row.MessageType), fixture.OutboxJson.Options)!;
}
