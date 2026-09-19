using Common.Application;
using Common.Contracts.Inventory.V1;
using Inventory.Application;
using Inventory.Domain.Reservations;
using Inventory.Domain.Reservations.Events;
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

    [Fact]
    public void The_three_reservation_events_become_their_contracts_correlated_on_the_order()
    {
        OrderId order = OrderId.New();
        ProductId short1 = ProductId.New();

        IReadOnlyList<object> mapped = Mapper().Map(
        [
            new StockReservedDomainEvent(order, Raised),
            new StockReservationFailedDomainEvent(order, [short1], Raised),
            new StockReleasedDomainEvent(order, Raised)
        ]);

        mapped.Count.ShouldBe(3);
        mapped[0].ShouldBeOfType<StockReserved>().OrderId.ShouldBe(order.Value);
        mapped[1].ShouldBeOfType<StockReservationFailed>().UnavailableProductIds.ShouldBe([short1.Value]);
        mapped[2].ShouldBeOfType<StockReleased>().CorrelationId.ShouldBe(order.Value);
    }
}
