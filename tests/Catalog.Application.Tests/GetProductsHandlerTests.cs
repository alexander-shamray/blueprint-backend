using Catalog.Application.Products.GetProducts;
using Catalog.Domain.Common;
using Catalog.Domain.Products;
using Catalog.Infrastructure.Persistence;
using Catalog.TestSupport;
using Common.Application;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Catalog.Application.Tests;

/// <summary>
/// §6.5's and ADR-016's four asserted behaviours — clamping, limit + 1, the
/// tiebreaker, the opaque cursor's round trip — each stated in prose there
/// and, until this file, asserted nowhere (§12 prescribes no pagination
/// test). Seeding goes through real aggregates and the DbContext (§12.4): a
/// raw INSERT drifts from the aggregate the first time it gains a column.
/// </summary>
[Collection(nameof(IntegrationCollection))]
public sealed class GetProductsHandlerTests(ServiceFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Base = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Seeds through the aggregate's own factory with a controlled clock —
    /// the only way to force the PublishedAt ties the tiebreaker tests need.
    /// </summary>
    private async Task<List<Product>> SeedAsync(params (string Name, DateTimeOffset PublishedAt)[] rows)
    {
        List<Product> products =
            [.. rows.Select(r => Product.Publish(r.Name, null, Money.Of(10m, "EUR"), r.PublishedAt))];

        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        CatalogDbContext db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        db.AddRange(products);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return products;
    }

    private async Task<CursorPage<ProductSummaryDto>> QueryAsync(string? cursor, int limit)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        return await dispatcher.QueryAsync(
            new GetProductsQuery(cursor, limit),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task An_empty_catalogue_is_an_empty_page_with_no_cursor()
    {
        CursorPage<ProductSummaryDto> page = await QueryAsync(null, 20);

        page.Items.ShouldBeEmpty();
        page.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task Products_come_back_newest_first()
    {
        await SeedAsync(
            ("Oldest", Base),
            ("Middle", Base.AddMinutes(1)),
            ("Newest", Base.AddMinutes(2)));

        CursorPage<ProductSummaryDto> page = await QueryAsync(null, 20);

        string[] names = [.. page.Items.Select(i => i.Name)];
        names.ShouldBe(["Newest", "Middle", "Oldest"]);
        page.NextCursor.ShouldBeNull("the page swallowed the whole table");
    }

    [Fact]
    public async Task The_cursor_walks_every_page_and_ends_null()
    {
        await SeedAsync(
            ("Oldest", Base),
            ("Middle", Base.AddMinutes(1)),
            ("Newest", Base.AddMinutes(2)));

        CursorPage<ProductSummaryDto> first = await QueryAsync(null, 2);

        first.Items.Select(i => i.Name).ShouldBe(["Newest", "Middle"]);
        first.NextCursor.ShouldNotBeNull("limit + 1 saw a third row");

        CursorPage<ProductSummaryDto> second = await QueryAsync(first.NextCursor, 2);

        second.Items.Select(i => i.Name).ShouldBe(["Oldest"]);
        second.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task Rows_sharing_a_publish_instant_never_straddle_the_boundary_twice()
    {
        // §6.5's tiebreaker rule, on a deliberate three-way tie: the seek is
        // (PublishedAt, Id) descending, so every row appears exactly once
        // across the boundary. The assertion is coverage, not a .NET sort —
        // SQL Server orders uniqueidentifier by its own byte groups (§5.2's
        // trap), and mirroring that here would test the engine, not the seek.
        List<Product> seeded = await SeedAsync(("A", Base), ("B", Base), ("C", Base));

        CursorPage<ProductSummaryDto> first = await QueryAsync(null, 2);
        CursorPage<ProductSummaryDto> second = await QueryAsync(first.NextCursor, 2);

        first.Items.Count.ShouldBe(2);
        second.Items.ShouldHaveSingleItem();
        second.NextCursor.ShouldBeNull();

        Guid[] seen = [.. first.Items.Concat(second.Items).Select(i => i.ProductId)];
        seen.ShouldBeUnique();
        seen.ShouldBe([.. seeded.Select(p => p.Id.Value)], ignoreOrder: true);
    }

    [Fact]
    public async Task The_limit_is_clamped_server_side_at_both_ends()
    {
        await SeedAsync([.. Enumerable.Range(0, 101).Select(i => ($"P{i}", Base.AddSeconds(i)))]);

        // A client asking for everything gets 100 (§6.5) …
        CursorPage<ProductSummaryDto> greedy = await QueryAsync(null, 100_000);
        greedy.Items.Count.ShouldBe(100);
        greedy.NextCursor.ShouldNotBeNull("row 101 is the next page");

        // … and one asking for nothing still gets a page of one.
        CursorPage<ProductSummaryDto> stingy = await QueryAsync(null, 0);
        stingy.Items.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_reported_product_lists_its_level_and_an_unreported_one_lists_null()
    {
        List<Product> seeded = await SeedAsync(("Reported", Base), ("Unreported", Base.AddMinutes(1)));
        Guid reported = seeded[0].Id.Value;
        Guid unreported = seeded[1].Id.Value;
        await fixture.ExecuteAsync(
            "INSERT INTO catalog.StockLevels (ProductId, QuantityAvailable, AsOf) VALUES ({0}, 4, SYSDATETIMEOFFSET())",
            reported);

        CursorPage<ProductSummaryDto> page = await QueryAsync(null, 20);

        page.Items.Single(p => p.ProductId == reported).QuantityAvailable.ShouldBe(4);
        page.Items.Single(p => p.ProductId == unreported).QuantityAvailable.ShouldBeNull(
            "unknown and none are different facts to a screen");
    }
}
