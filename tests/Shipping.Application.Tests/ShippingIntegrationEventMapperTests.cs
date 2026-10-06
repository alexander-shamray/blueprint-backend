using Common.Application;
using Common.Contracts.Shipping.V1;
using Common.Domain;
using Microsoft.Extensions.DependencyInjection;
using Shipping.Application;
using Shipping.Application.Integration;
using Shipping.Domain.Shipments;
using Shipping.Domain.Shipments.Events;
using Shouldly;
using Xunit;

namespace Shipping.Application.Tests;

public class ShippingIntegrationEventMapperTests
{
    private static readonly DateTimeOffset Raised = new(2026, 9, 22, 9, 0, 0, TimeSpan.Zero);

    private static IIntegrationEventMapper Mapper()
    {
        ServiceCollection services = new();
        services.AddShippingApplication();
        return services
            .BuildServiceProvider()
            .CreateScope()
            .ServiceProvider
            .GetRequiredService<IIntegrationEventMapper>();
    }

    [Fact]
    public void A_despatch_becomes_ShipmentDispatched_correlated_on_the_order()
    {
        OrderId order = new(Guid.CreateVersion7());

        ShipmentDispatched contract = Mapper()
            .Map([new ShipmentDispatchedDomainEvent(ShipmentId.New(), order, "TRK1", Raised)])
            .ShouldHaveSingleItem()
            .ShouldBeOfType<ShipmentDispatched>();

        contract.OrderId.ShouldBe(order.Value);
        contract.CorrelationId.ShouldBe(order.Value, "§9.6's saga correlates on the order");
        contract.TrackingNumber.ShouldBe("TRK1");
        contract.OccurredAt.ShouldBe(Raised);
        contract.MessageId.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public void A_delivery_becomes_ShipmentDelivered_correlated_on_the_order()
    {
        OrderId order = new(Guid.CreateVersion7());

        ShipmentDelivered contract = Mapper()
            .Map([new ShipmentDeliveredDomainEvent(ShipmentId.New(), order, "TRK1", Raised)])
            .ShouldHaveSingleItem()
            .ShouldBeOfType<ShipmentDelivered>();

        contract.OrderId.ShouldBe(order.Value);
        contract.CorrelationId.ShouldBe(order.Value);
        contract.TrackingNumber.ShouldBe("TRK1");
    }

    [Fact]
    public void One_commit_raising_both_reaches_both_contracts_in_order()
    {
        OrderId order = new(Guid.CreateVersion7());

        IReadOnlyList<object> mapped = Mapper().Map(
            [
                new ShipmentDispatchedDomainEvent(ShipmentId.New(), order, "TRK1", Raised),
                new ShipmentDeliveredDomainEvent(ShipmentId.New(), order, "TRK1", Raised.AddHours(1))
            ]);

        // Order, not membership: the mapper stages contracts in the order raised; delivery is the outbox's (§9.4).
        mapped.Select(c => c.GetType()).ShouldBe([typeof(ShipmentDispatched), typeof(ShipmentDelivered)]);
    }

    [Fact]
    public void The_registry_is_exactly_the_publishes_column()
    {
        // §3.2 gives Shipping two published contracts and §10.7 no tracking event; asserted over the registry, as a
        // Map over a hand-written list finds only the entries the list names.
        ShippingIntegrationEventMapper.RegisteredEvents.ShouldBe(
            [typeof(ShipmentDispatchedDomainEvent), typeof(ShipmentDeliveredDomainEvent)],
            ignoreOrder: true);
    }

    [Fact]
    public void An_entry_in_the_registry_reaches_its_contract()
    {
        // Each maps to the contract §3.2's Publishes column gives it, which right keys alone would not show.
        OrderId order = new(Guid.CreateVersion7());

        Mapper().Map(
            [
                new ShipmentDispatchedDomainEvent(ShipmentId.New(), order, "TRK1", Raised),
                new ShipmentDeliveredDomainEvent(ShipmentId.New(), order, "TRK1", Raised),
                new UnpublishedDomainEvent(Raised)
            ])
            .Select(c => c.GetType().FullName)
            .ShouldBe(
                ["Common.Contracts.Shipping.V1.ShipmentDispatched", "Common.Contracts.Shipping.V1.ShipmentDelivered"],
                ignoreOrder: true);
    }

    [Fact]
    public void An_unregistered_domain_event_reaches_no_contract()
    {
        Mapper()
            .Map([new UnpublishedDomainEvent(Raised)])
            .ShouldBeEmpty("an unregistered domain event is local-only, and that is not an error");
    }

    private sealed record UnpublishedDomainEvent(DateTimeOffset OccurredAt) : IDomainEvent;
}
