using Catalog.Application.Products;
using Catalog.Application.Products.GetProduct;
using Catalog.Application.Products.GetProducts;
using Catalog.Domain.Common;
using Catalog.Domain.Products;
using Catalog.Infrastructure.Persistence;
using Catalog.TestSupport;
using Common.Application;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Catalog.Application.Tests;

/// <summary>§6.5's one-product read: the listing's row by id, else <see cref="ProductErrors.NotFound"/>.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class GetProductHandlerTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<Guid> SeedAsync(string name)
    {
        Product product = Product.Publish(name, null, Money.Of(10m, "EUR"), DateTimeOffset.UtcNow);

        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        CatalogDbContext db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        db.Add(product);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return product.Id.Value;
    }

    private async Task<Result<ProductSummaryDto>> QueryAsync(Guid productId)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        return await dispatcher.QueryAsync(new GetProductQuery(productId), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_reported_product_carries_its_level_and_an_unreported_one_null()
    {
        Guid reported = await SeedAsync("Reported");
        Guid unreported = await SeedAsync("Unreported");
        await fixture.ExecuteAsync(
            "INSERT INTO catalog.StockLevels (ProductId, QuantityAvailable, AsOf) VALUES ({0}, 4, SYSDATETIMEOFFSET())",
            reported);

        Result<ProductSummaryDto> withLevel = await QueryAsync(reported);
        Result<ProductSummaryDto> withoutLevel = await QueryAsync(unreported);

        withLevel.Value.Name.ShouldBe("Reported");
        withLevel.Value.QuantityAvailable.ShouldBe(4);
        withoutLevel.Value.QuantityAvailable.ShouldBeNull("unknown and none are different facts to a screen");
    }

    [Fact]
    public async Task An_unknown_id_is_the_not_found_error()
    {
        await SeedAsync("Present");

        Result<ProductSummaryDto> result = await QueryAsync(Guid.CreateVersion7());

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(ProductErrors.NotFound);
    }
}
