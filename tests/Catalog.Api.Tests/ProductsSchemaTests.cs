using Catalog.Infrastructure.Persistence;
using Catalog.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Catalog.Api.Tests;

/// <summary>§7.4's backward compatibility for the products table, in a file the scaffold does not copy.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class ProductsSchemaTests(ServiceFixture fixture)
{
    [Fact]
    public async Task A_row_the_previous_release_inserts_without_LastEventAt_still_commits()
    {
        // The previous release's insert names no LastEventAt; without the column's default it fails NOT NULL.
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        CatalogDbContext db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Guid id = Guid.CreateVersion7();

        int stamped = await db.Database.CreateExecutionStrategy().ExecuteAsync(
            async token =>
            {
                // Rolled back, so no product read sharing this database sees the row.
                await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(token);

                await db.Database.ExecuteSqlAsync(
                    $"""
                    INSERT INTO catalog.Products (Id, Name, ThumbnailUrl, PriceAmount, PriceCurrency, PublishedAt)
                    VALUES ({id}, N'Previous release', NULL, 1, 'EUR', SYSDATETIMEOFFSET());
                    """,
                    token);

                int count = await db.Database
                    .SqlQuery<int>(
                        $"SELECT Value = COUNT(*) FROM catalog.Products WHERE Id = {id} AND LastEventAt IS NOT NULL")
                    .SingleAsync(token);

                await transaction.RollbackAsync(token);
                return count;
            },
            ct);

        stamped.ShouldBe(1);
    }
}
