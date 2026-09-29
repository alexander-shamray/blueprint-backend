using Shipping.Domain.Shipments;
using Shipping.Infrastructure.Retention;
using Shipping.TestSupport;
using Shouldly;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>
/// ADR-053's two windows against the tables they are about: an address is
/// deleted its window after its shipment turns terminal, and a shipment's
/// tracking events theirs after delivery. The shipment's own record survives
/// both, which is why the address is a table of its own (spec, section 7).
/// </summary>
[Collection(nameof(IntegrationCollection))]
public sealed class ShippingRetentionTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task An_address_outlives_a_live_shipment_and_not_a_terminal_one_past_its_window()
    {
        // The live row is arranged second, and the order is the arrangement:
        // DeliveredAsync runs a tracking pass, and that claim takes every
        // Booked row whose poll is due — a shipment booked before it would be
        // delivered by it and stop being the live one this asserts over.
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
        // The two clocks are not one: an address goes on any terminal state and
        // tracking events only after a delivery, because a voided shipment's
        // feed is the record of what the carrier did with a parcel nobody
        // received.
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
        // Spec section 11: no line the purge pass logs holds an address. Its
        // lines carry a row count and a table name, and neither is a person.
        Shipment terminal = await fixture.DeliveredAsync(line1: "12 Абай даңғылы", city: "Алматы");
        await fixture.AgeTerminalAsync(terminal.Id, TimeSpan.FromDays(12));

        await fixture.PurgeShippingRetentionAsync();

        // CapturedLogs.Everything and not a search over formatted messages: it
        // holds the message, the state's values and any exception's
        // ToString(), so the structured half is searched too (spec, section 11).
        fixture.CapturedLogs.Everything.ShouldNotBeEmpty(
            "a capture that recorded nothing would pass whatever the pass logged");
        fixture.CapturedLogs.Everything.ShouldContain(
            line => line.Contains("Shipping retention deleted", StringComparison.Ordinal),
            "the pass's own line must be in the capture, or the absence below proves nothing");
        fixture.CapturedLogs.Everything.ShouldNotContain(
            line => line.Contains("Абай", StringComparison.Ordinal)
                || line.Contains("Алматы", StringComparison.Ordinal));
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
