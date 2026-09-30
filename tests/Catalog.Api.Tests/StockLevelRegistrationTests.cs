using Catalog.Infrastructure.Messaging;
using Common.Contracts.Inventory.V1;
using Common.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Catalog.Api.Tests;

/// <summary>§3.2's Consumes column for Catalog, as the exact set of consumer registrations.</summary>
public class StockLevelRegistrationTests
{
    [Fact]
    public void Catalog_binds_exactly_the_one_consumer_in_its_consumes_column()
    {
        ServiceCollection services = new();

        services.AddMassTransitMessaging(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    [
                        new KeyValuePair<string, string?>(
                            "ConnectionStrings:RabbitMq",
                            "amqp://guest:guest@catalog-rabbit.invalid:5672")
                    ])
                .Build());

        services
            .Where(MessagingRegistrationTests.IsConsumerRegistration)
            .Select(d => d.ImplementationType ?? d.ServiceType)
            .Distinct()
            .ShouldBe(
                [typeof(IntegrationEventConsumer<StockLevelChanged>)],
                "§3.2 gives Catalog one Consumes cell, StockLevelChanged, and a second consumer here is a " +
                "subscription the table does not give it");
    }
}
