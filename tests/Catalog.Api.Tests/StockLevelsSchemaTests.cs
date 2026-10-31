using Catalog.TestSupport;
using Shouldly;
using Xunit;

namespace Catalog.Api.Tests;

/// <summary>§3.2's one Catalog projection table, in a file the scaffold does not copy.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class StockLevelsSchemaTests(ServiceFixture fixture)
{
    [Fact]
    public async Task The_migrator_creates_the_stock_level_projection()
    {
        (await fixture.ScalarAsync<int>(
            """
            SELECT Value = COUNT(*)
            FROM sys.tables
            WHERE schema_id = SCHEMA_ID('catalog')
                AND name = 'StockLevels'
            """))
            .ShouldBe(1);
    }

    [Fact]
    public async Task No_two_stock_levels_share_a_page()
    {
        // Two rows at least, since one row fills one page whatever its width (ADR-183); a deleted row's page may
        // outlive it, so pages may outnumber rows.
        await fixture.ExecuteAsync(
            """
            INSERT INTO catalog.StockLevels (ProductId, QuantityAvailable, AsOf)
            VALUES (NEWID(), 1, SYSDATETIMEOFFSET()), (NEWID(), 2, SYSDATETIMEOFFSET())
            """);

        int rows = await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM catalog.StockLevels");
        long pages = await fixture.ScalarAsync<long>(
            """
            SELECT Value = in_row_data_page_count
            FROM sys.dm_db_partition_stats
            WHERE object_id = OBJECT_ID('catalog.StockLevels')
                AND index_id = 1
            """);

        pages.ShouldBeGreaterThanOrEqualTo(rows);
    }
}
