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

/// <summary>§6.5's and ADR-016's pagination, and ADR-073's search and sort over it.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class GetProductsHandlerTests(ServiceFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Base = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Seeds through the aggregate and the DbContext (§12.4), with the PublishedAt each row names.</summary>
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

    private async Task<CursorPage<ProductSummaryDto>> QueryAsync(
        string? cursor,
        int limit,
        string? q = null,
        string? sort = null)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        return await dispatcher.QueryAsync(
            new GetProductsQuery(cursor, limit, q, sort),
            TestContext.Current.CancellationToken);
    }

    /// <summary>Every page of one query, walked with the cursors it returns, so a skip or a repeat shows.</summary>
    private async Task<List<ProductSummaryDto>> WalkAsync(int limit, string? q = null, string? sort = null)
    {
        List<ProductSummaryDto> seen = [];
        string? cursor = null;
        do
        {
            CursorPage<ProductSummaryDto> page = await QueryAsync(cursor, limit, q, sort);
            seen.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        return seen;
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
        // §6.5's tiebreaker on a three-way tie, asserted as coverage rather than a .NET sort, since SQL Server
        // orders uniqueidentifier by its own byte groups (§5.2).
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
        int ceiling = GetProductsHandler.MaxLimit;
        await SeedAsync([.. Enumerable.Range(0, ceiling + 1).Select(i => ($"P{i}", Base.AddSeconds(i)))]);

        // A client asking for everything gets the ceiling (§6.5) …
        CursorPage<ProductSummaryDto> greedy = await QueryAsync(null, 100_000);
        greedy.Items.Count.ShouldBe(ceiling);
        greedy.NextCursor.ShouldNotBeNull("the row past the ceiling is the next page");

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

    [Fact]
    public async Task A_search_matches_anywhere_in_the_name_whatever_its_case()
    {
        await SeedAsync(
            ("Walnut desk lamp", Base),
            ("Oak DESK", Base.AddMinutes(1)),
            ("Floor lamp", Base.AddMinutes(2)));

        CursorPage<ProductSummaryDto> page = await QueryAsync(null, 20, q: "desk");

        page.Items.Select(i => i.Name).ShouldBe(["Oak DESK", "Walnut desk lamp"]);
    }

    [Fact]
    public async Task A_search_reads_the_like_wildcards_as_the_characters_they_are()
    {
        // ADR-073: q is text to find, never a pattern, so % and _ and [ match only themselves.
        await SeedAsync(
            ("100% wool", Base),
            ("1000 wool", Base.AddMinutes(1)),
            ("a_b", Base.AddMinutes(2)),
            ("axb", Base.AddMinutes(3)),
            ("[x]", Base.AddMinutes(4)),
            ("x", Base.AddMinutes(5)));

        (await QueryAsync(null, 20, q: "0%")).Items.Select(i => i.Name).ShouldBe(["100% wool"]);
        (await QueryAsync(null, 20, q: "a_b")).Items.Select(i => i.Name).ShouldBe(["a_b"]);
        (await QueryAsync(null, 20, q: "[x]")).Items.Select(i => i.Name).ShouldBe(["[x]"]);
    }

    [Fact]
    public async Task A_search_nothing_matches_is_an_empty_page_with_no_cursor()
    {
        await SeedAsync(("Walnut desk lamp", Base));

        CursorPage<ProductSummaryDto> page = await QueryAsync(null, 20, q: "sofa");

        page.Items.ShouldBeEmpty();
        page.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task The_name_sort_pages_a_filtered_set_in_order_with_nothing_skipped_or_repeated()
    {
        // Ties on the name, and one row the search excludes, so both the tiebreaker and the filter cross a page.
        List<Product> seeded = await SeedAsync(
            ("Lamp C", Base),
            ("Lamp A", Base.AddMinutes(1)),
            ("Lamp B", Base.AddMinutes(2)),
            ("Lamp A", Base.AddMinutes(3)),
            ("Lamp A", Base.AddMinutes(4)),
            ("Chair", Base.AddMinutes(5)));

        List<ProductSummaryDto> seen = await WalkAsync(2, q: "lamp", sort: ProductSort.Name);

        seen.Select(i => i.Name).ShouldBe(["Lamp A", "Lamp A", "Lamp A", "Lamp B", "Lamp C"]);
        seen.Select(i => i.ProductId).ShouldBeUnique();
        seen.Select(i => i.ProductId).ShouldBe(
            [.. seeded.Where(p => p.Name != "Chair").Select(p => p.Id.Value)],
            ignoreOrder: true);
    }

    [Fact]
    public async Task The_newest_sort_pages_a_filtered_set_with_nothing_skipped_or_repeated()
    {
        await SeedAsync(
            ("Lamp 1", Base),
            ("Chair", Base.AddMinutes(1)),
            ("Lamp 2", Base.AddMinutes(2)),
            ("Lamp 3", Base.AddMinutes(3)),
            ("Chair", Base.AddMinutes(4)));

        List<ProductSummaryDto> seen = await WalkAsync(1, q: "lamp");

        seen.Select(i => i.Name).ShouldBe(["Lamp 3", "Lamp 2", "Lamp 1"]);
    }

    [Fact]
    public async Task A_cursor_reused_under_another_query_is_refused_rather_than_misread()
    {
        await SeedAsync(("Lamp A", Base), ("Lamp B", Base.AddMinutes(1)), ("Lamp C", Base.AddMinutes(2)));
        CursorPage<ProductSummaryDto> first = await QueryAsync(null, 1, q: "lamp", sort: ProductSort.Name);

        // The pipeline's ValidationBehavior throws before the handler reads anything (§6.3).
        await Should.ThrowAsync<FluentValidation.ValidationException>(
            () => QueryAsync(first.NextCursor, 1, q: "lamp", sort: ProductSort.Newest));
        await Should.ThrowAsync<FluentValidation.ValidationException>(
            () => QueryAsync(first.NextCursor, 1, q: "lam", sort: ProductSort.Name));
    }
}
