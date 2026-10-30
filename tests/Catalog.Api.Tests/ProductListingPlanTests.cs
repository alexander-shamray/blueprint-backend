using System.Data;
using System.Xml.Linq;
using Catalog.Application.Products.GetProducts;
using Catalog.TestSupport;
using Microsoft.Data.SqlClient;
using Shouldly;
using Xunit;

namespace Catalog.Api.Tests;

/// <summary>
/// ADR-073's walk: each ordering reads its own index from the cursor and stops after <c>limit + 1</c> rows, so a
/// page never scans the table and sorts it, whatever the table holds.
/// </summary>
[Collection(nameof(IntegrationCollection))]
public sealed class ProductListingPlanTests(ServiceFixture fixture)
{
    // Enough rows that a scan and sort undercuts a walk paying a lookup a row, as on the stress run's catalogue.
    private const int Products = 300;

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
        XElement[] plan = await PlanAsync(sort, afterCursor);
        XElement[] products = plan
            .Where(op => op.Descendants(Showplan + "Object").Any(o => (string?)o.Attribute("Table") == "[Products]"))
            .Where(op => op.Elements().Any(e => e.Name == Showplan + "IndexScan"))
            .ToArray();

        XElement read = products.ShouldHaveSingleItem();
        read.Element(Showplan + "IndexScan")!.Element(Showplan + "Object")!.Attribute("Index")!.Value.ShouldBe(index);
        plan.Select(op => (string)op.Attribute("PhysicalOp")!).ShouldNotContain("Sort");

        // From the cursor, a seek; on the first page, a walk from the index's end that the TOP stops.
        string expected = afterCursor ? "Index Seek" : "Index Scan";
        ((string)read.Attribute("PhysicalOp")!).ShouldBe(expected);
    }

    private static readonly XNamespace Showplan = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    // Run for real under STATISTICS XML, so the parameters are sniffed as the handler's are, and inside a
    // transaction rolled back, so no listing sharing this database sees the rows.
    private async Task<XElement[]> PlanAsync(string sort, bool afterCursor)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using SqlConnection connection = new(fixture.ConnectionString);
        await connection.OpenAsync(ct);
        await using SqlTransaction transaction = connection.BeginTransaction();

        await using (SqlCommand seed = new(
            $"""
            INSERT INTO catalog.Products (Id, Name, ThumbnailUrl, PriceAmount, PriceCurrency, PublishedAt)
            SELECT NEWID(), CONCAT(N'Plan product ', n), N'https://images.example.test/plan.png', 10, 'EUR',
                DATEADD(MINUTE, -n, SYSDATETIMEOFFSET())
            FROM (SELECT TOP ({Products}) n = ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) FROM sys.all_objects) s;

            INSERT INTO catalog.StockLevels (ProductId, QuantityAvailable, AsOf)
            SELECT Id, 5, SYSDATETIMEOFFSET()
            FROM catalog.Products;
            """,
            connection,
            transaction))
        {
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
                ORDER BY PublishedAt DESC, Id DESC
                OFFSET {Products / 2} ROWS FETCH NEXT 1 ROWS ONLY;
                """,
                connection,
                transaction);
            await using SqlDataReader reader = await middle.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            after = (reader.GetDateTimeOffset(0), reader.GetString(1), reader.GetGuid(2));
        }

        await using (SqlCommand on = new("SET STATISTICS XML ON;", connection, transaction))
            await on.ExecuteNonQueryAsync(ct);

        // A text of its own, so the instance's cache cannot hand back a plan another test compiled.
        string sql = $"-- {Guid.NewGuid()}{Environment.NewLine}{GetProductsHandler.Sql(sort, afterCursor)}";
        await using SqlCommand listing = new(sql, connection, transaction);

        // The parameters as Dapper declares them from the handler's anonymous object.
        listing.Parameters.Add(new SqlParameter("@Take", SqlDbType.Int) { Value = 21 });
        listing.Parameters.Add(new SqlParameter("@Pattern", SqlDbType.NVarChar, 4000) { Value = DBNull.Value });
        listing.Parameters.Add(new SqlParameter("@AfterId", SqlDbType.UniqueIdentifier)
        {
            Value = (object?)after?.Id ?? DBNull.Value
        });
        if (sort == ProductSort.Name)
        {
            listing.Parameters.Add(new SqlParameter("@AfterName", SqlDbType.NVarChar, 4000)
            {
                Value = (object?)after?.Name ?? DBNull.Value
            });
        }
        else
        {
            listing.Parameters.Add(new SqlParameter("@AfterPublishedAt", SqlDbType.DateTimeOffset)
            {
                Value = (object?)after?.PublishedAt ?? DBNull.Value
            });
        }

        string? showplan = null;
        await using (SqlDataReader reader = await listing.ExecuteReaderAsync(ct))
        {
            do
            {
                while (await reader.ReadAsync(ct))
                {
                    if (reader.FieldCount == 1 && reader.GetName(0).StartsWith("Microsoft SQL Server", StringComparison.Ordinal))
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
