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
            "SELECT Value = COUNT(*) FROM sys.tables WHERE schema_id = SCHEMA_ID('catalog') AND name = 'StockLevels'"))
            .ShouldBe(1);
    }
}
