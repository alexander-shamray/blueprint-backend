using Common.Application;
using Common.Contracts.Inventory.V1;
using Inventory.Application;
using Inventory.Domain.Stock;
using Inventory.Domain.Stock.Events;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Inventory.Application.Tests;

public class InventoryIntegrationEventMapperTests
{
    private static readonly DateTimeOffset Raised = new(2026, 9, 18, 9, 0, 0, TimeSpan.Zero);

    private static IIntegrationEventMapper Mapper()
    {
        ServiceCollection services = new();
        services.AddInventoryApplication();
        return services.BuildServiceProvider().CreateScope().ServiceProvider
            .GetRequiredService<IIntegrationEventMapper>();
    }

    [Fact]
    public void A_level_change_becomes_its_contract_with_the_product_as_correlation()
    {
        ProductId product = ProductId.New();

        IReadOnlyList<object> mapped = Mapper().Map([new StockLevelChangedDomainEvent(product, 7, Raised)]);

        StockLevelChanged contract = mapped.ShouldHaveSingleItem().ShouldBeOfType<StockLevelChanged>();
        contract.ProductId.ShouldBe(product.Value);
        contract.QuantityAvailable.ShouldBe(7);
        contract.CorrelationId.ShouldBe(product.Value);
        contract.OccurredAt.ShouldBe(Raised);
        contract.MessageId.ShouldNotBe(Guid.Empty);
    }
}
