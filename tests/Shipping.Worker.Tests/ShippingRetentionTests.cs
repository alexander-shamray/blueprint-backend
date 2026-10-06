using Shipping.Domain.Shipments;
using Shipping.Infrastructure.Retention;
using Shipping.Infrastructure.Tracking;
using Shipping.TestSupport;
using Shouldly;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>ADR-053's two windows against their tables; the shipment's own record survives both.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class ShippingRetentionTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task An_address_outlives_a_live_shipment_and_not_a_terminal_one_past_its_window()
    {
        // The live row is arranged second, since DeliveredAsync's tracking pass would deliver an earlier booking.
        Shipment terminal = await fixture.DeliveredAsync();
        Shipment live = await fixture.BookedAsync("050000");
        await fixture.AgeTerminalAsync(terminal.Id, TimeSpan.FromDays(12));

        (int addresses, _) = await fixture.PurgeShippingRetentionAsync();

        addresses.ShouldBe(1);
        (await fixture.AddressCountAsync(live.OrderId)).ShouldBe(1, "a shipment still moving still needs its address");
        (await fixture.AddressCountAsync(terminal.OrderId)).ShouldBe(0);
        (await fixture.StatusAsync(terminal.Id)).ShouldBe("Delivered", "the shipment's own record is whole");
    }

    [Fact]
    public async Task A_shipment_the_carrier_never_finished_loses_its_address_on_the_same_window()
    {
        // ADR-054's outer bound is what starts the window for a parcel never
        // delivered: abandoned by the tracking pass, then aged past the window.
        Shipment shipment = await fixture.BookedAsync("SIM-TRANSIT");
        await fixture.AgeCreatedAsync(shipment.Id, TrackingWorker.GiveUpAge + TimeSpan.FromMinutes(1));
        (await fixture.RunTrackingPassAsync()).ShouldBe(1);
        (await fixture.StatusAsync(shipment.Id)).ShouldBe("Abandoned");
        await fixture.AgeTerminalAsync(shipment.Id, TimeSpan.FromDays(12));

        (int addresses, _) = await fixture.PurgeShippingRetentionAsync();

        addresses.ShouldBe(1);
        (await fixture.AddressCountAsync(shipment.OrderId)).ShouldBe(0);
    }

    [Fact]
    public async Task An_address_inside_its_window_is_kept()
    {
        Shipment terminal = await fixture.DeliveredAsync();
        await fixture.AgeTerminalAsync(terminal.Id, TimeSpan.FromDays(10));

        (int addresses, _) = await fixture.PurgeShippingRetentionAsync();

        addresses.ShouldBe(0, "eleven days is the invented deployment's window and ten is inside it");
        (await fixture.AddressCountAsync(terminal.OrderId)).ShouldBe(1);
    }

    [Fact]
    public async Task Tracking_events_go_on_their_own_window_and_not_the_address_s()
    {
        Shipment terminal = await fixture.DeliveredAsync();
        await fixture.AgeTerminalAsync(terminal.Id, TimeSpan.FromDays(12));

        (int addresses, int events) = await fixture.PurgeShippingRetentionAsync();

        addresses.ShouldBe(1);
        events.ShouldBe(0, "twenty-three days is the tracking window, and twelve is inside it");

        await fixture.AgeTerminalAsync(terminal.Id, TimeSpan.FromDays(24));
        (_, int later) = await fixture.PurgeShippingRetentionAsync();

        later.ShouldBeGreaterThan(0);
        (await fixture.TrackingEventCountAsync(terminal.Id)).ShouldBe(0);
        (await fixture.StatusAsync(terminal.Id)).ShouldBe("Delivered");
    }

    [Fact]
    public async Task A_voided_shipments_events_are_not_deleted_by_the_delivery_window()
    {
        // An address goes on any terminal state, tracking events only after a delivery.
        Shipment voided = await fixture.VoidedWithTrackingAsync();
        await fixture.AgeTerminalAsync(voided.Id, TimeSpan.FromDays(40));

        (int addresses, int events) = await fixture.PurgeShippingRetentionAsync();

        addresses.ShouldBe(1);
        events.ShouldBe(0);
        (await fixture.TrackingEventCountAsync(voided.Id)).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task No_line_of_the_purge_holds_an_address()
    {
        // No line the purge logs holds an address (ADR-052).
        Shipment terminal = await fixture.DeliveredAsync(line1: "12 Абай даңғылы", city: "Алматы");
        await fixture.AgeTerminalAsync(terminal.Id, TimeSpan.FromDays(12));

        await fixture.PurgeShippingRetentionAsync();

        // CapturedLogs.Everything, so the state's values and any exception are searched too.
        fixture.CapturedLogs.Everything.ShouldNotBeEmpty(
            "a capture that recorded nothing would pass whatever the pass logged");
        fixture.CapturedLogs.Everything.ShouldContain(
            line => line.Contains("Shipping retention deleted", StringComparison.Ordinal),
            "the pass's own line must be in the capture, or the absence below proves nothing");
        fixture.CapturedLogs.Everything.ShouldNotContain(
            line => line.Contains("Абай", StringComparison.Ordinal) ||
                line.Contains("Алматы", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_backlog_larger_than_one_batch_drains_within_the_pass()
    {
        const int backlog = ShippingRetentionService.BatchSize + 100;
        await fixture.StageExpiredDeliveriesAsync(backlog);

        (int addresses, int events) = await fixture.PurgeShippingRetentionAsync();

        addresses.ShouldBe(backlog);
        events.ShouldBe(backlog);
        (await fixture.AddressTotalAsync()).ShouldBe(0);
        (await fixture.ShipmentCountAsync()).ShouldBe(backlog, "the shipments' own records survive");
    }
}
