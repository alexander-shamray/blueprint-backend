using Common.Infrastructure.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Shipping.Domain.Shipments;
using Shipping.Infrastructure.Carrier;
using Shipping.Infrastructure.Fulfilment;
using Shipping.Infrastructure.Tracking;
using Shipping.TestSupport;
using Shouldly;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>The tracking worker against a real database and the simulator: what one pass claims and leaves.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class TrackingWorkerTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public void The_lease_outlives_the_hop_and_the_pass()
    {
        // A pass polls its rows together, so CarrierHop.TotalRequestTimeout bounds the pass; a shorter lease would let
        // a second replica claim a row still being polled.
        TimeSpan lease = TimeSpan.FromSeconds(TrackingWorker.LeaseSeconds);

        lease.ShouldBeGreaterThan(CarrierHop.TotalRequestTimeout, "a pass must finish inside its own lease");
        CarrierHop.TotalRequestTimeout.ShouldBeLessThan(
            TimeSpan.FromSeconds(30),
            "the host's shutdown timeout is thirty seconds (§15.3), and a pass that outlives it is killed mid-row");

        // NextPollAt is stamped part-way into a tick, so a tick equal to the interval would take a due row a tick late.
        CarrierHop.TrackingTick.ShouldBeLessThan(
            CarrierHop.TrackingPollInterval,
            "the tick bounds how late a due row is claimed, and one as long as the interval doubles the cadence");

        // Separate leases over one column, each bounding its own pass; LockedUntil keeps the two workers apart.
        TimeSpan.FromSeconds(FulfilmentWorker.LeaseSeconds).ShouldBeGreaterThan(
            TimeSpan.FromSeconds(TrackingWorker.LeaseSeconds),
            "a fulfilment pass makes two hops and a tracking pass one, so its lease is the longer");
    }

    [Fact]
    public async Task A_booked_shipment_is_polled_and_a_collected_page_despatches_it()
    {
        Shipment shipment = await fixture.BookedAsync("SIM-TRANSIT");

        (await Worker().ProcessBatchAsync(TestContext.Current.CancellationToken)).ShouldBe(1);

        (await fixture.StatusAsync(shipment.Id)).ShouldBe("Dispatched");
        (await fixture.LockedUntilAsync(shipment.Id)).ShouldBeNull(
            "the pass that claimed the row released it through Shipment.PollApplied");
        (await fixture.OutboxAsync())
            .Select(row => row.MessageType)
            .ShouldContain(type => type.Contains("ShipmentDispatched", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_delivered_page_stages_the_despatch_first_and_stops_the_polling()
    {
        Shipment shipment = await fixture.BookedAsync("050000");

        (await Worker().ProcessBatchAsync(TestContext.Current.CancellationToken)).ShouldBe(1);

        (await fixture.StatusAsync(shipment.Id)).ShouldBe("Delivered");
        (await fixture.NextPollAtAsync(shipment.Id)).ShouldBeNull(
            "a terminal shipment has nothing further to learn");
        (await fixture.LockedUntilAsync(shipment.Id)).ShouldBeNull(
            "a terminal row is released like any other, not left to its lease");

        // Order, not membership: Ordering's saga finalises on the first and ADR-051's projection reads both. Read off
        // the identity column, as both rows carry one recording instant (Shipment.Record).
        (await fixture.OutboxAsync())
            .OrderBy(row => row.Id)
            .Select(row => row.MessageType.Split('.')[^1])
            .ShouldBe(["ShipmentDispatched", "ShipmentDelivered"]);
    }

    [Fact]
    public async Task A_refused_cancellation_goes_on_being_tracked_and_despatches_on_collection()
    {
        // SIM-LATE's cancel answers too late, and its feed is the simulator's default page, collected then delivered.
        Shipment shipment = await fixture.BookedAsync("SIM-LATE");
        await fixture.RequestCancellationAsync(shipment.Id);

        (await fixture.RunFulfilmentPassAsync()).ShouldBe(1);
        (await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM shipping.Shipments WHERE Id = {0} AND CancellationRefusedAt IS NOT NULL",
            shipment.Id.Value)).ShouldBe(1, "the carrier refused the cancellation");

        // Still Booked with both stamps, so only the refusal predicate keeps the fulfilment claim off it; counted as
        // carrier calls, as a re-asked cancellation would move nothing either.
        int carrierCalls = fixture.Carrier.LogEntries.Count;
        await fixture.RunFulfilmentPassAsync();
        fixture.Carrier.LogEntries.Count.ShouldBe(carrierCalls, "a refused cancellation is outside the claim");

        // Read off the clock the handler stamps with, on either side of the
        // pass, so the bound below is the pass's own duration and no skew.
        TimeProvider clock = fixture.Factory.Services.GetRequiredService<TimeProvider>();
        DateTimeOffset before = clock.GetUtcNow();

        (await Worker().ProcessBatchAsync(TestContext.Current.CancellationToken)).ShouldBe(1);

        DateTimeOffset after = clock.GetUtcNow();

        (await fixture.StatusAsync(shipment.Id)).ShouldBe("Delivered");
        IReadOnlyList<OutboxMessage> outbox = await fixture.OutboxAsync();
        outbox
            .OrderBy(row => row.Id)
            .Select(row => row.MessageType.Split('.')[^1])
            .ShouldBe(["ShipmentDispatched", "ShipmentDelivered"]);

        // The despatch carries the instant the pass raised it: §9.4 stamps the row with it, and §13.3's lag reads it.
        outbox
            .Single(row => row.MessageType.EndsWith("ShipmentDispatched", StringComparison.Ordinal))
            .OccurredAt.ShouldBeInRange(before, after);
    }

    [Fact]
    public async Task A_row_still_being_polled_is_not_claimed_by_a_second_pass()
    {
        Shipment shipment = await fixture.BookedAsync("SIM-TRANSIT");

        // Staged rather than two passes, since the second must meet a row in flight.
        await fixture.ClaimForTrackingAsync();

        (await Worker().ProcessBatchAsync(TestContext.Current.CancellationToken)).ShouldBe(0);

        // A pass that claimed the row and failed also answers 0, but leaves PollAttempts at 1.
        (await fixture.StatusAsync(shipment.Id)).ShouldBe("Booked");
        (await fixture.PollAttemptsAsync(shipment.Id)).ShouldBe(0);
    }

    [Fact]
    public async Task A_shipment_past_its_tracking_age_is_abandoned_without_asking_the_carrier()
    {
        // ADR-054: asked before the carrier is, so the age holds however long
        // the carrier stays silent. SIM-TRANSIT's page would despatch the row,
        // so a Booked row read back as Abandoned is the pass not polling it.
        Shipment shipment = await fixture.BookedAsync("SIM-TRANSIT");
        await fixture.AgeCreatedAsync(shipment.Id, TrackingWorker.GiveUpAge + TimeSpan.FromMinutes(1));
        int carrierCalls = fixture.Carrier.LogEntries.Count;

        (await Worker().ProcessBatchAsync(TestContext.Current.CancellationToken)).ShouldBe(1);

        (await fixture.StatusAsync(shipment.Id)).ShouldBe("Abandoned");
        fixture.Carrier.LogEntries.Count.ShouldBe(carrierCalls, "past the age the carrier is not asked");
        (await fixture.NextPollAtAsync(shipment.Id)).ShouldBeNull();
        (await fixture.LockedUntilAsync(shipment.Id)).ShouldBeNull("the commit that ended the row released it");
        (await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM shipping.Shipments WHERE Id = {0} AND TerminalAt IS NOT NULL",
            shipment.Id.Value)).ShouldBe(1, "ADR-053's address window starts at TerminalAt");
        (await fixture.OutboxAsync()).ShouldBeEmpty("nothing on the platform waits on a delivery that never came");
        fixture.CapturedLogs.Everything.ShouldContain(
            line => line.Contains("is abandoned and no longer polled", StringComparison.Ordinal));

        (await Worker().ProcessBatchAsync(TestContext.Current.CancellationToken)).ShouldBe(
            0, "a terminal row is outside the claim");
    }

    [Fact]
    public async Task A_shipment_inside_its_tracking_age_is_still_polled()
    {
        // The control for the case above: a minute short of the age, and the
        // page is applied as any other.
        Shipment shipment = await fixture.BookedAsync("SIM-TRANSIT");
        await fixture.AgeCreatedAsync(shipment.Id, TrackingWorker.GiveUpAge - TimeSpan.FromMinutes(1));

        (await Worker().ProcessBatchAsync(TestContext.Current.CancellationToken)).ShouldBe(1);

        (await fixture.StatusAsync(shipment.Id)).ShouldBe("Dispatched");
    }

    [Fact]
    public async Task A_poll_that_succeeds_leaves_the_fulfilment_pass_s_count_alone()
    {
        // A cancellation that keeps failing climbs its own ladder while the
        // feed answers (ADR-054): a poll clearing the fulfilment count would
        // hold the cancel at the ladder's first steps for the whole outage.
        Shipment shipment = await fixture.BookedAsync("SIM-TRANSIT");
        await fixture.RequestCancellationAsync(shipment.Id);
        await fixture.SetAttemptsAsync(shipment.Id, 3);
        await fixture.SetPollAttemptsAsync(shipment.Id, 2);

        (await Worker().ProcessBatchAsync(TestContext.Current.CancellationToken)).ShouldBe(1);

        (await fixture.AttemptsAsync(shipment.Id)).ShouldBe(3);
        (await fixture.PollAttemptsAsync(shipment.Id)).ShouldBe(0, "an applied page resets this worker's own ladder");
    }

    [Fact]
    public async Task A_lapsed_lease_is_taken_by_another_pass()
    {
        Shipment shipment = await fixture.BookedAsync("SIM-TRANSIT");

        await fixture.ClaimForTrackingAsync();
        await fixture.ExpireLeasesAsync();

        (await Worker().ProcessBatchAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        (await fixture.StatusAsync(shipment.Id)).ShouldBe("Dispatched");
    }

    [Fact]
    public async Task A_row_the_fulfilment_worker_holds_is_not_polled()
    {
        // A Booked row with an unanswered cancellation is due to both claims; TrackingClaims' LockedUntil predicate
        // keeps this worker off it.
        Shipment shipment = await fixture.BookedAsync("SIM-TRANSIT");
        await fixture.RequestCancellationAsync(shipment.Id);

        await fixture.ClaimForFulfilmentAsync();

        (await Worker().ProcessBatchAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
        (await fixture.LockedUntilAsync(shipment.Id)).ShouldNotBeNull(
            "the tracking pass skipped the row rather than releasing a lease it does not hold");
    }

    [Fact]
    public async Task A_row_this_worker_holds_is_not_claimed_by_a_fulfilment_pass()
    {
        // The same row under this worker's lease, refused by FulfilmentClaims' own LockedUntil predicate.
        Shipment shipment = await fixture.BookedAsync("SIM-TRANSIT");
        await fixture.RequestCancellationAsync(shipment.Id);

        await fixture.ClaimForTrackingAsync();

        (await fixture.RunFulfilmentPassAsync()).ShouldBe(0);
        (await fixture.StatusAsync(shipment.Id)).ShouldBe(
            "Booked", "nothing cancelled at the carrier while this worker held the row");
    }

    [Fact]
    public async Task A_pass_that_throws_leaves_the_host_running()
    {
        // The claim failing, with the database unreachable, is the case ExecuteAsync's filter exists for.
        using ShippingWorkerFactory broken = new(Unreachable.Sql, Unreachable.Rabbit);

        TrackingWorker worker = broken.Services.GetRequiredService<TrackingWorker>();

        // The pass itself throws, which a carrier outage never does: that is caught per row.
        await Should.ThrowAsync<Exception>(
            () => worker.ProcessBatchAsync(TestContext.Current.CancellationToken));

        await worker.StartAsync(TestContext.Current.CancellationToken);

        // Staged on the loop's own line, which the first CarrierHop.TrackingTick reaches inside the wait's deadline.
        await ServiceFixture.WaitUntilAsync(() =>
            Task.FromResult(ClaimFailedLogged(broken) || worker.ExecuteTask!.IsCompleted));

        // ExecuteTask is the loop, and a faulted one is the host on its way
        // down: the default BackgroundServiceExceptionBehavior stops it.
        worker.ExecuteTask!.IsFaulted.ShouldBeFalse();
        ClaimFailedLogged(broken).ShouldBeTrue();

        await worker.StopAsync(TestContext.Current.CancellationToken);
    }

    private static bool ClaimFailedLogged(ShippingWorkerFactory host) =>
        host.CapturedLogs.Everything.Any(line => line.StartsWith("Tracking claim failed", StringComparison.Ordinal));

    private TrackingWorker Worker() => fixture.Factory.Services.GetRequiredService<TrackingWorker>();
}
