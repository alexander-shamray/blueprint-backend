using Catalog.Application.Products;
using Catalog.Application.Products.GetProduct;
using Catalog.Application.Products.GetProducts;
using Catalog.Domain.Common;
using Catalog.Domain.Products;
using Catalog.Infrastructure.Persistence;
using Catalog.TestSupport;
using Common.Application;
using Common.Contracts.Inventory.V1;
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

    private async Task HandleAsync<TEvent>(TEvent domainEvent)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IProjectionHandler<TEvent> handler = scope.ServiceProvider.GetRequiredService<IProjectionHandler<TEvent>>();

        await handler.HandleAsync(domainEvent, TestContext.Current.CancellationToken);
    }

    private Task SetPriceAsync(Guid productId, decimal amount) =>
        fixture.ExecuteAsync("UPDATE catalog.Products SET PriceAmount = {0} WHERE Id = {1}", amount, productId);

    [Fact]
    public async Task A_second_read_is_served_from_the_cache_though_the_row_has_changed()
    {
        Guid id = await SeedAsync("Cached");
        (await QueryAsync(id)).Value.Amount.ShouldBe(10m);

        await SetPriceAsync(id, 99m);

        (await QueryAsync(id)).Value.Amount.ShouldBe(10m, "nothing has invalidated the key yet (§8.2)");
    }

    [Fact]
    public async Task A_price_change_drops_the_cached_row()
    {
        Guid id = await SeedAsync("Repriced");
        await QueryAsync(id);
        await SetPriceAsync(id, 24.5m);

        await HandleAsync(
            new PriceChangedDomainEvent(new ProductId(id), Money.Of(24.5m, "EUR"), DateTimeOffset.UtcNow));

        (await QueryAsync(id)).Value.Amount.ShouldBe(24.5m);
    }

    [Fact]
    public async Task A_withdrawal_drops_the_cached_row_so_the_read_is_the_not_found_error()
    {
        Guid id = await SeedAsync("Withdrawn");
        await QueryAsync(id);
        await fixture.ExecuteAsync("UPDATE catalog.Products SET WithdrawnAt = SYSDATETIMEOFFSET() WHERE Id = {0}", id);

        await HandleAsync(new ProductDiscontinuedDomainEvent(new ProductId(id), DateTimeOffset.UtcNow));

        (await QueryAsync(id)).Error.ShouldBe(ProductErrors.NotFound);
    }

    [Fact]
    public async Task A_stock_level_drops_the_cached_row_after_it_is_written()
    {
        Guid id = await SeedAsync("Restocked");
        (await QueryAsync(id)).Value.QuantityAvailable.ShouldBeNull();

        await using (AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IIntegrationEventHandler<StockLevelChanged>>().HandleAsync(
                new StockLevelChanged
                {
                    MessageId = Guid.CreateVersion7(),
                    CorrelationId = id,
                    OccurredAt = DateTimeOffset.UtcNow,
                    ProductId = id,
                    QuantityAvailable = 7
                },
                TestContext.Current.CancellationToken);
        }

        (await QueryAsync(id)).Value.QuantityAvailable.ShouldBe(7);
    }

    [Fact]
    public async Task A_miss_is_not_kept_so_the_row_that_appears_later_is_read()
    {
        Guid id = await SeedAsync("Soon");
        await fixture.ExecuteAsync("UPDATE catalog.Products SET WithdrawnAt = SYSDATETIMEOFFSET() WHERE Id = {0}", id);
        (await QueryAsync(id)).IsFailure.ShouldBeTrue();

        await fixture.ExecuteAsync("UPDATE catalog.Products SET WithdrawnAt = NULL WHERE Id = {0}", id);

        (await QueryAsync(id)).IsSuccess.ShouldBeTrue("a cached miss would hide a row no event removes");
    }

    [Fact]
    public async Task Both_own_events_have_a_projection_so_the_dispatcher_stages_their_local_rows()
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IProjectionRegistry registry = scope.ServiceProvider.GetRequiredService<IProjectionRegistry>();
        var product = new ProductId(Guid.CreateVersion7());

        registry
            .HasHandler(new PriceChangedDomainEvent(product, Money.Of(1m, "EUR"), DateTimeOffset.UtcNow))
            .ShouldBeTrue();
        registry.HasHandler(new ProductDiscontinuedDomainEvent(product, DateTimeOffset.UtcNow)).ShouldBeTrue();
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
