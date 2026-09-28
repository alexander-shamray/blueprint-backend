using Shipping.Application.Integration;
using Shouldly;
using Xunit;

namespace Shipping.Application.Tests;

public class ShippingIntegrationEventMapperTests
{
    [Fact]
    public void The_registry_is_empty_because_nothing_here_promotes_a_shipment()
    {
        // §9.3's allow-list is §3.2's Publishes column, and both contracts are
        // raised by a tracking event that promotes the shipment — which is a
        // carrier's fact and reaches no code in this service yet. An entry added
        // before that would put a domain event on the bus with nothing to raise
        // it (§5.5).
        ShippingIntegrationEventMapper.RegisteredEvents.ShouldBeEmpty();
    }
}
