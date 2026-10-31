using System.Data;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using Ordering.TestSupport;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>
/// §6.6's claim for <c>IX_OrderSummaries_Customer_PlacedAt</c>: the history page seeks the customer's rows in the
/// page's whole order, so neither page sorts, and a cursor's page seeks to the cursor.
/// </summary>
[Collection(nameof(IntegrationCollection))]
public sealed class OrderSummaryPagePlanTests(ServiceFixture fixture)
{
    // Enough rows that a customer's share is a range worth seeking into, and a scan dearer than a lookup a row.
    private const int Summaries = 20000;

    private static readonly XNamespace Showplan = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    // §6.6's two statements as the chapter writes them; no handler in src/ sends them yet (§6.5).
    private const string Columns =
        """
        SELECT TOP (@Take) OrderId, Status, Total = TotalAmount, Currency, LineCount, PlacedAt, Products
        FROM ordering.OrderSummaries
        WHERE CustomerId = @CustomerId
        """;

    private const string Newest =
        """

        ORDER BY PlacedAt DESC, OrderId DESC;
        """;

    private const string FirstSql = Columns + Newest;

    private const string AfterSql =
        Columns +
        """

            AND PlacedAt <= @AfterPlacedAt
            AND (PlacedAt < @AfterPlacedAt OR OrderId < @AfterId)
        """ +
        Newest;

    // The keyset as Catalog's and the BFF's cursor pages write it, which seeks to the cursor on their indexes.
    private const string OrAfterSql =
        Columns +
        """

            AND (PlacedAt < @AfterPlacedAt
                OR (PlacedAt = @AfterPlacedAt AND OrderId < @AfterId))
        """ +
        Newest;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_history_page_seeks_the_customer_index_with_no_sort(bool afterCursor)
    {
        XElement[] plan = await PlanAsync(afterCursor ? AfterSql : FirstSql, afterCursor);

        // Keyed without OrderId DESC, the index's order is the clustered key's ascending one, and the page sorts.
        plan.Select(op => (string)op.Attribute("PhysicalOp")!).ShouldNotContain("Sort");

        // The cursor bounds the seek's range, not a residual predicate after it.
        string[] seekColumns = SeekColumns(plan);
        (seekColumns.Contains("PlacedAt") && seekColumns.Contains("@AfterPlacedAt")).ShouldBe(afterCursor);
    }

    // Why §6.6 spells the bound out: over this index the OR form seeks the customer alone and reads every earlier
    // row of theirs. A planner that seeks it here fails this, and the bound can go from the chapter and the test.
    [Fact]
    public async Task The_OR_form_alone_leaves_the_cursor_residual_on_this_index()
    {
        XElement[] plan = await PlanAsync(OrAfterSql, afterCursor: true);

        // PlacedAt absent from the seek in any form, a dynamic range's included, so any seek on it fails this.
        SeekColumns(plan).ShouldNotContain("PlacedAt");
    }

    private static string[] SeekColumns(XElement[] plan)
    {
        XElement seek = plan
            .Where(op => op.Elements().Any(e => e.Name == Showplan + "IndexScan"))
            .Where(op => (string?)op.Element(Showplan + "IndexScan")!.Element(Showplan + "Object")!.Attribute("Index")
                == "[IX_OrderSummaries_Customer_PlacedAt]")
            .ShouldHaveSingleItem();
        ((string)seek.Attribute("PhysicalOp")!).ShouldBe("Index Seek");

        return
        [
            .. seek
                .Element(Showplan + "IndexScan")!
                .Elements(Showplan + "SeekPredicates")
                .Descendants(Showplan + "ColumnReference")
                .Select(c => (string?)c.Attribute("Column") ?? "")
        ];
    }

    // Run for real under STATISTICS XML, so the parameters are sniffed as Dapper's would be, and inside a
    // transaction rolled back, so no test sharing this database sees the rows.
    private async Task<XElement[]> PlanAsync(string statement, bool afterCursor)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Guid customer = Guid.NewGuid();
        await using SqlConnection connection = new(fixture.ConnectionString);
        await connection.OpenAsync(ct);
        await using SqlTransaction transaction = connection.BeginTransaction();

        // One row in twenty is this run's customer's and the rest are spread over others.
        await using (SqlCommand seed = new(
            $"""
            INSERT INTO ordering.OrderSummaries
                (OrderId, Status, UpdatedAt, CustomerId, TotalAmount, Currency, LineCount, Products, PlacedAt)
            SELECT
                NEWID(),
                'Placed',
                SYSDATETIMEOFFSET(),
                CASE WHEN n % 20 = 0 THEN @Customer ELSE NEWID() END,
                10,
                'EUR',
                1,
                N'[]',
                DATEADD(MINUTE, -n, SYSDATETIMEOFFSET())
            FROM (
                SELECT TOP ({Summaries}) n = ROW_NUMBER() OVER (ORDER BY (SELECT NULL))
                FROM sys.all_objects a
                CROSS JOIN sys.all_objects b
            ) s;
            """,
            connection,
            transaction))
        {
            seed.Parameters.Add(new SqlParameter("@Customer", SqlDbType.UniqueIdentifier) { Value = customer });
            await seed.ExecuteNonQueryAsync(ct);
        }

        // A middle row, so the cursor's page has rows on both sides of it.
        (DateTimeOffset PlacedAt, Guid Id)? after = null;
        if (afterCursor)
        {
            await using SqlCommand middle = new(
                $"""
                SELECT PlacedAt, OrderId
                FROM ordering.OrderSummaries
                WHERE CustomerId = @Customer
                ORDER BY PlacedAt DESC, OrderId DESC
                OFFSET {Summaries / 40} ROWS FETCH NEXT 1 ROWS ONLY;
                """,
                connection,
                transaction);
            middle.Parameters.Add(new SqlParameter("@Customer", SqlDbType.UniqueIdentifier) { Value = customer });
            await using SqlDataReader reader = await middle.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            after = (reader.GetDateTimeOffset(0), reader.GetGuid(1));
        }

        await using (SqlCommand on = new("SET STATISTICS XML ON;", connection, transaction))
            await on.ExecuteNonQueryAsync(ct);

        // A text of its own, so the instance's cache cannot hand back a plan another test compiled.
        string unique = $"-- {Guid.NewGuid()}{Environment.NewLine}{statement}";
        await using SqlCommand page = new(unique, connection, transaction);

        // The parameters as Dapper declares them from the chapter's anonymous object, each only where named.
        SqlParameter[] parameters =
        [
            new("@Take", SqlDbType.Int) { Value = 21 },
            new("@CustomerId", SqlDbType.UniqueIdentifier) { Value = customer },
            new("@AfterId", SqlDbType.UniqueIdentifier) { Value = (object?)after?.Id ?? DBNull.Value },
            new("@AfterPlacedAt", SqlDbType.DateTimeOffset) { Value = (object?)after?.PlacedAt ?? DBNull.Value }
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
