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
        // The narrowest row against half a page's 8096 bytes, since two rows wider than that cannot share one
        // (ADR-183); a page count would also count the empty pages other tests' deletes leave behind.
        await fixture.ExecuteAsync(
            """
            INSERT INTO catalog.StockLevels (ProductId, QuantityAvailable, AsOf)
            VALUES (NEWID(), 1, SYSDATETIMEOFFSET())
            """);

        int narrowest = await fixture.ScalarAsync<int>(
            """
            SELECT Value = MIN(min_record_size_in_bytes)
            FROM sys.dm_db_index_physical_stats(DB_ID(), OBJECT_ID('catalog.StockLevels'), 1, NULL, 'DETAILED')
            WHERE index_level = 0
            """);

        narrowest.ShouldBeGreaterThan(8096 / 2);
    }
}
