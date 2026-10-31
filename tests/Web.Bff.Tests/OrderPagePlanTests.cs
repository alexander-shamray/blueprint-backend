using System.Data;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using Shouldly;
using Web.Bff.Orders;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>
/// The buyer's order page, held as ADR-179 holds Catalog's: each page seeks <c>IX_Orders_Owned</c> with no sort, and
/// a cursor's page seeks to the cursor rather than to the buyer's newest row.
/// </summary>
[Collection(nameof(BffIntegrationCollection))]
public sealed class OrderPagePlanTests(BffServiceFixture fixture)
{
    // Enough rows that a buyer's share is a range worth seeking into, not the whole table.
    private const int Orders = 1000;

    private static readonly XNamespace Showplan = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_buyer_s_page_seeks_its_cursor_on_the_owned_index_with_no_sort(bool afterCursor)
    {
        XElement[] plan = await PlanAsync(
            afterCursor ? OrderReader.AfterPageSql : OrderReader.FirstPageSql,
            afterCursor);

        XElement seek = plan
            .Where(op => op.Elements().Any(e => e.Name == Showplan + "IndexScan"))
            .Where(op => (string?)op.Element(Showplan + "IndexScan")!.Element(Showplan + "Object")!.Attribute("Index")
                == "[IX_Orders_Owned]")
            .ShouldHaveSingleItem();
        ((string)seek.Attribute("PhysicalOp")!).ShouldBe("Index Seek");
        plan.Select(op => (string)op.Attribute("PhysicalOp")!).ShouldNotContain("Sort");

        // Under "@After IS NULL OR", FirstSeenAt is a residual predicate and the seek starts at the buyer's newest.
        bool seeksFirstSeenAt = seek
            .Element(Showplan + "IndexScan")!
            .Elements(Showplan + "SeekPredicates")
            .Descendants(Showplan + "ColumnReference")
            .Any(c => (string?)c.Attribute("Column") == "FirstSeenAt");
        seeksFirstSeenAt.ShouldBe(afterCursor);
    }

    // Run for real under STATISTICS XML, so the parameters are sniffed as the reader's are, and inside a transaction
    // rolled back, so no test sharing this database sees the rows.
    private async Task<XElement[]> PlanAsync(string statement, bool afterCursor)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Guid buyer = Guid.NewGuid();
        await using SqlConnection connection = new(fixture.ConnectionString);
        await connection.OpenAsync(ct);
        await using SqlTransaction transaction = connection.BeginTransaction();

        // One row in twenty is this run's buyer's and the rest are spread over others, as a buyer's share of a store.
        await using (SqlCommand seed = new(
            $"""
            INSERT INTO bff.Orders (OrderId, CustomerId, FirstSeenAt, AsOf)
            SELECT NEWID(), CASE WHEN n % 20 = 0 THEN @Buyer ELSE NEWID() END,
                DATEADD(MINUTE, -n, SYSDATETIMEOFFSET()), SYSDATETIMEOFFSET()
            FROM (SELECT TOP ({Orders}) n = ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) FROM sys.all_objects) s;
            """,
            connection,
            transaction))
        {
            seed.Parameters.Add(new SqlParameter("@Buyer", SqlDbType.UniqueIdentifier) { Value = buyer });
            await seed.ExecuteNonQueryAsync(ct);
        }

        // A middle row, so the cursor's page has rows on both sides of it.
        (DateTimeOffset FirstSeenAt, Guid Id)? after = null;
        if (afterCursor)
        {
            await using SqlCommand middle = new(
                $"""
                SELECT FirstSeenAt, OrderId
                FROM bff.Orders
                WHERE CustomerId = @Buyer
                ORDER BY FirstSeenAt DESC, OrderId DESC
                OFFSET {Orders / 40} ROWS FETCH NEXT 1 ROWS ONLY;
                """,
                connection,
                transaction);
            middle.Parameters.Add(new SqlParameter("@Buyer", SqlDbType.UniqueIdentifier) { Value = buyer });
            await using SqlDataReader reader = await middle.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            after = (reader.GetDateTimeOffset(0), reader.GetGuid(1));
        }

        await using (SqlCommand on = new("SET STATISTICS XML ON;", connection, transaction))
            await on.ExecuteNonQueryAsync(ct);

        // A text of its own, so the instance's cache cannot hand back a plan another test compiled.
        string unique = $"-- {Guid.NewGuid()}{Environment.NewLine}{statement}";
        await using SqlCommand page = new(unique, connection, transaction);

        // The parameters as Dapper declares them from the reader's anonymous object, each only where named.
        SqlParameter[] parameters =
        [
            new("@Take", SqlDbType.Int) { Value = 21 },
            new("@CustomerId", SqlDbType.UniqueIdentifier) { Value = buyer },
            new("@AfterId", SqlDbType.UniqueIdentifier) { Value = (object?)after?.Id ?? DBNull.Value },
            new("@AfterFirstSeenAt", SqlDbType.DateTimeOffset) { Value = (object?)after?.FirstSeenAt ?? DBNull.Value }
        ];
        page.Parameters.AddRange(
            parameters.Where(p => statement.Contains(p.ParameterName, StringComparison.Ordinal)).ToArray());

        string? showplan = null;
        await using (SqlDataReader reader = await page.ExecuteReaderAsync(ct))
        {
            do
            {
                // STATISTICS XML follows the page's rows with one column named for the showplan schema.
                bool isPlan = reader.FieldCount == 1
                    && reader.GetName(0).StartsWith("Microsoft SQL Server", StringComparison.Ordinal);
                while (await reader.ReadAsync(ct))
                {
                    if (isPlan)
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
