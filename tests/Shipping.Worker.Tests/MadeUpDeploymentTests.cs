using Shipping.Domain.Shipments;
using Shipping.TestSupport;
using Shouldly;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>ADR-053 rule 2: the suite passes under invented windows and an address country no country uses.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class MadeUpDeploymentTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task An_address_in_ZZ_books_at_the_carrier()
    {
        // Address already constructs ZZ, and nothing between the store and the simulator may learn a country (ADR-053).
        Shipment shipment = await fixture.BookedAsync(postalCode: "050000", country: "ZZ");

        (await fixture.StatusAsync(shipment.Id)).ShouldBe("Booked");
        (await fixture.CarrierReferenceAsync(shipment.Id)).ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task The_whole_tracking_and_retention_path_runs_under_the_invented_windows()
    {
        Shipment shipment = await fixture.BookedAsync(postalCode: "050000", country: "ZZ");

        await fixture.RunTrackingPassAsync();

        (await fixture.StatusAsync(shipment.Id)).ShouldBe("Delivered");

        await fixture.AgeTerminalAsync(shipment.Id, TimeSpan.FromDays(24));
        (int addresses, int events) = await fixture.PurgeShippingRetentionAsync();

        addresses.ShouldBe(1);
        events.ShouldBeGreaterThan(0);
    }
}
