using Microsoft.EntityFrameworkCore.Migrations;

namespace Catalog.Infrastructure.Persistence.Migrations;

/// <summary>
/// Lets each listing order walk its index without a lookup a row (ADR-178). IncludeProperties cannot name a
/// complex type's columns, so this is SQL and the model snapshot does not record it; DROP_EXISTING rebuilds each
/// index in place, so no read runs without it.
/// </summary>
public partial class CoverProductListingIndexes : Migration
{
    /// <summary>The two indexes with the listing's columns included.</summary>
    internal const string Covered =
        """
        CREATE INDEX IX_Products_PublishedAt_Id ON catalog.Products (PublishedAt, Id)
        INCLUDE (Name, ThumbnailUrl, PriceAmount, PriceCurrency, WithdrawnAt)
        WITH (DROP_EXISTING = ON);

        CREATE INDEX IX_Products_Name_Id ON catalog.Products (Name, Id)
        INCLUDE (ThumbnailUrl, PriceAmount, PriceCurrency, PublishedAt, WithdrawnAt)
        WITH (DROP_EXISTING = ON);
        """;

    /// <summary>The two indexes on their keys alone, as AddProductNameIndex left them.</summary>
    internal const string Uncovered =
        """
        CREATE INDEX IX_Products_PublishedAt_Id ON catalog.Products (PublishedAt, Id)
        WITH (DROP_EXISTING = ON);

        CREATE INDEX IX_Products_Name_Id ON catalog.Products (Name, Id)
        WITH (DROP_EXISTING = ON);
        """;

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Covered);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Uncovered);
}
