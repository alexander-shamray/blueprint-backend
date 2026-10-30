using Microsoft.EntityFrameworkCore.Migrations;

namespace Catalog.Infrastructure.Persistence.Migrations;

/// <summary>Lets each listing order walk its index without a lookup a row (ADR-179).</summary>
/// <remarks>
/// SQL because IncludeProperties cannot name a complex type's columns, so the snapshot does not record it;
/// DROP_EXISTING rebuilds each index in place, so no read runs without it (§7.4).
/// </remarks>
public partial class CoverProductListingIndexes : Migration
{
    /// <summary>The three listing indexes with their reads' columns included.</summary>
    internal const string Covered =
        """
        CREATE INDEX IX_Products_PublishedAt_Id ON catalog.Products (PublishedAt, Id)
        INCLUDE (Name, ThumbnailUrl, PriceAmount, PriceCurrency, WithdrawnAt)
        WITH (DROP_EXISTING = ON);

        CREATE INDEX IX_Products_Name_Id ON catalog.Products (Name, Id)
        INCLUDE (ThumbnailUrl, PriceAmount, PriceCurrency, PublishedAt, WithdrawnAt)
        WITH (DROP_EXISTING = ON);

        CREATE INDEX IX_Products_SellerId_PublishedAt_Id ON catalog.Products (SellerId, PublishedAt, Id)
        INCLUDE (Name, ThumbnailUrl, PriceAmount, PriceCurrency, WithdrawnAt)
        WITH (DROP_EXISTING = ON);
        """;

    /// <summary>The three indexes on their keys alone, as the migrations before this one left them.</summary>
    internal const string Uncovered =
        """
        CREATE INDEX IX_Products_PublishedAt_Id ON catalog.Products (PublishedAt, Id)
        WITH (DROP_EXISTING = ON);

        CREATE INDEX IX_Products_Name_Id ON catalog.Products (Name, Id)
        WITH (DROP_EXISTING = ON);

        CREATE INDEX IX_Products_SellerId_PublishedAt_Id ON catalog.Products (SellerId, PublishedAt, Id)
        WITH (DROP_EXISTING = ON);
        """;

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Covered);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Uncovered);
}
