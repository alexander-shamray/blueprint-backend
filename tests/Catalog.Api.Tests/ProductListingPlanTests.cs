using System.Data;
using System.Xml.Linq;
using Catalog.Application.Products.GetOwnProducts;
using Catalog.Application.Products.GetProducts;
using Catalog.TestSupport;
using Microsoft.Data.SqlClient;
using Shouldly;
using Xunit;

namespace Catalog.Api.Tests;

/// <summary>
/// ADR-073's walk: each ordering reads its own index from the cursor and stops after <c>limit + 1</c> rows, so a
/// page never scans the table and sorts it, whatever the table holds; and a seller's own list seeks to its cursor.
/// </summary>
[Collection(nameof(IntegrationCollection))]
public sealed class ProductListingPlanTests(ServiceFixture fixture)
{
    // Enough rows that a scan and sort undercuts a walk paying a lookup a row, as on the stress run's catalogue.
    private const int Products = 300;

    private static readonly XNamespace Showplan = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    public static TheoryData<string, string, bool> Pages() => new()
    {
        { ProductSort.Newest, "[IX_Products_PublishedAt_Id]", false },
        { ProductSort.Newest, "[IX_Products_PublishedAt_Id]", true },
        { ProductSort.Name, "[IX_Products_Name_Id]", false },
        { ProductSort.Name, "[IX_Products_Name_Id]", true }
    };

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task A_page_walks_its_ordering_s_index_with_no_sort_and_no_lookup(
        string sort,
        string index,
        bool afterCursor)
    {
        XElement[] plan = await PlanAsync(GetProductsHandler.Sql(sort, afterCursor), afterCursor);

        XElement read = Reads(plan).ShouldHaveSingleItem();
        IndexOf(read).ShouldBe(index);
        plan.Select(op => (string)op.Attribute("PhysicalOp")!).ShouldNotContain("Sort");

        // From the cursor, a seek; on the first page, a walk from the index's end that the TOP stops.
        string expected = afterCursor ? "Index Seek" : "Index Scan";
        ((string)read.Attribute("PhysicalOp")!).ShouldBe(expected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_seller_s_page_seeks_its_cursor_on_the_seller_index_with_no_lookup(bool afterCursor)
    {
        XElement[] plan = await PlanAsync(
            afterCursor ? GetOwnProductsHandler.AfterSql : GetOwnProductsHandler.FirstSql,
            afterCursor);

        XElement seek = Reads(plan).ShouldHaveSingleItem();
        IndexOf(seek).ShouldBe("[IX_Products_SellerId_PublishedAt_Id]");
        ((string)seek.Attribute("PhysicalOp")!).ShouldBe("Index Seek");
        plan.Select(op => (string)op.Attribute("PhysicalOp")!).ShouldNotContain("Sort");

        // Under "@After IS NULL OR", PublishedAt is a residual predicate and the seek starts at the seller's newest.
        bool seeksPublishedAt = seek
            .Element(Showplan + "IndexScan")!
            .Elements(Showplan + "SeekPredicates")
            .Descendants(Showplan + "ColumnReference")
            .Any(c => (string?)c.Attribute("Column") == "PublishedAt");
        seeksPublishedAt.ShouldBe(afterCursor);
    }

    private static IEnumerable<XElement> Reads(XElement[] plan) =>
        plan
            .Where(op => op.Elements().Any(e => e.Name == Showplan + "IndexScan"))
            .Where(op => (string?)op.Element(Showplan + "IndexScan")!.Element(Showplan + "Object")!.Attribute("Table")
                == "[Products]");

    private static string IndexOf(XElement read) =>
        read.Element(Showplan + "IndexScan")!.Element(Showplan + "Object")!.Attribute("Index")!.Value;

    // Run for real under STATISTICS XML, so the parameters are sniffed as the handler's are, and inside a
    // transaction rolled back, so no listing sharing this database sees the rows.
    private async Task<XElement[]> PlanAsync(string statement, bool afterCursor)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Guid seller = Guid.NewGuid();
        await using SqlConnection connection = new(fixture.ConnectionString);
        await connection.OpenAsync(ct);
        await using SqlTransaction transaction = connection.BeginTransaction();

        // One row in ten is this run's seller's and the rest are spread over others, so the seller index is as
        // selective as a seller's share of a catalogue; the seeded names alone get stock levels, whatever is here.
        await using (SqlCommand seed = new(
            $"""
            INSERT INTO catalog.Products (Id, Name, ThumbnailUrl, PriceAmount, PriceCurrency, PublishedAt, SellerId)
            SELECT NEWID(), CONCAT(N'Plan product {seller:N} ', n), N'https://images.example.test/plan.png', 10, 'EUR',
                DATEADD(MINUTE, -n, SYSDATETIMEOFFSET()), CASE WHEN n % 10 = 0 THEN @Seller ELSE NEWID() END
            FROM (SELECT TOP ({Products}) n = ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) FROM sys.all_objects) s;

            INSERT INTO catalog.StockLevels (ProductId, QuantityAvailable, AsOf)
            SELECT Id, 5, SYSDATETIMEOFFSET()
            FROM catalog.Products
            WHERE Name LIKE N'Plan product {seller:N} %';
            """,
            connection,
            transaction))
        {
            seed.Parameters.Add(new SqlParameter("@Seller", SqlDbType.UniqueIdentifier) { Value = seller });
            await seed.ExecuteNonQueryAsync(ct);
        }

        // A middle row, so the cursor's page has rows on both sides of it.
        (DateTimeOffset PublishedAt, string Name, Guid Id)? after = null;
        if (afterCursor)
        {
            await using SqlCommand middle = new(
                $"""
                SELECT PublishedAt, Name, Id
                FROM catalog.Products
                WHERE SellerId = @Seller
                ORDER BY PublishedAt DESC, Id DESC
                OFFSET {Products / 20} ROWS FETCH NEXT 1 ROWS ONLY;
                """,
                connection,
                transaction);
            middle.Parameters.Add(new SqlParameter("@Seller", SqlDbType.UniqueIdentifier) { Value = seller });
            await using SqlDataReader reader = await middle.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            after = (reader.GetDateTimeOffset(0), reader.GetString(1), reader.GetGuid(2));
        }

        await using (SqlCommand on = new("SET STATISTICS XML ON;", connection, transaction))
            await on.ExecuteNonQueryAsync(ct);

        // A text of its own, so the instance's cache cannot hand back a plan another test compiled.
        string unique = $"-- {Guid.NewGuid()}{Environment.NewLine}{statement}";
        await using SqlCommand listing = new(unique, connection, transaction);

        // The parameters as Dapper declares them from the handlers' anonymous objects, each only where named.
        SqlParameter[] parameters =
        [
            new("@Take", SqlDbType.Int) { Value = 21 },
            new("@Pattern", SqlDbType.NVarChar, 4000) { Value = DBNull.Value },
            new("@SellerId", SqlDbType.UniqueIdentifier) { Value = seller },
            new("@AfterId", SqlDbType.UniqueIdentifier) { Value = (object?)after?.Id ?? DBNull.Value },
            new("@AfterName", SqlDbType.NVarChar, 4000) { Value = (object?)after?.Name ?? DBNull.Value },
            new("@AfterPublishedAt", SqlDbType.DateTimeOffset) { Value = (object?)after?.PublishedAt ?? DBNull.Value }
        ];
        listing.Parameters.AddRange(
            parameters.Where(p => statement.Contains(p.ParameterName, StringComparison.Ordinal)).ToArray());

        string? showplan = null;
        await using (SqlDataReader reader = await listing.ExecuteReaderAsync(ct))
        {
            do
            {
                // STATISTICS XML follows the listing's rows with one column named for the showplan schema.
                bool plan = reader.FieldCount == 1
                    && reader.GetName(0).StartsWith("Microsoft SQL Server", StringComparison.Ordinal);
                while (await reader.ReadAsync(ct))
                {
                    if (plan)
                        showplan = reader.GetString(0);
                }
            }
            while (await reader.NextResultAsync(ct));
        }

        await transaction.RollbackAsync(ct);

        showplan.ShouldNotBeNull();
        return XDocument.Parse(showplan).Descendants(Showplan + "RelOp").ToArray();
    }
}
