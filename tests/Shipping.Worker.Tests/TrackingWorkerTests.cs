using Microsoft.Extensions.DependencyInjection;
using Shipping.Domain.Shipments;
using Shipping.Infrastructure.Carrier;
using Shipping.Infrastructure.Fulfilment;
using Shipping.Infrastructure.Tracking;
using Shipping.TestSupport;
using Shouldly;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>
/// The second worker against a real database and the simulator's own mappings:
/// what one pass claims, what it leaves, and which rows it will not take
/// (spec, sections 4 and 9).
/// </summary>
[Collection(nameof(IntegrationCollection))]
public sealed class TrackingWorkerTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public void The_lease_outlives_the_hop_and_the_pass()
    {
        // CarrierHop.TotalRequestTimeout bounds one call; the lease bounds the
        // pass around it (spec, section 4). A lease shorter than either would
        // let a second replica claim a row this pass is still calling the
        // carrier about, and the two would record against the same aggregate.
        TimeSpan lease = TimeSpan.FromSeconds(TrackingWorker.LeaseSeconds);

        lease.ShouldBeGreaterThan(CarrierHop.TotalRequestTimeout, "one call must finish inside the lease");
        lease.ShouldBeGreaterThan(TrackingWorker.PassBudget, "a pass must finish inside its own lease");
        TrackingWorker.PassBudget.ShouldBeGreaterThan(
            CarrierHop.TotalRequestTimeout,
            "a budget below one hop's total would make every pass claim rows and process none");
        TrackingWorker.PassBudget.ShouldBeLessThan(
            TimeSpan.FromSeconds(30),
            "the host's shutdown timeout is thirty seconds (§15.3), and a pass that outlives it is killed mid-row");

        // NextPollAt is stamped after the carrier answers, part-way into a
        // tick, so a loop ticking once per poll interval meets that row a
        // moment before it is due and takes it one tick late (CarrierHop).
        CarrierHop.TrackingTick.ShouldBeLessThan(
            CarrierHop.TrackingPollInterval,
            "the tick bounds how late a due row is claimed, and one as long as the interval doubles the cadence");

        // The two leases are separate numbers over one column: each bounds its
        // own worst-case pass, and neither is a bound on the other's. What
        // keeps the two workers apart is the LockedUntil predicate
        // TrackingClaims and FulfilmentClaims both carry; the lengths only
        // decide how long a killed replica's row waits.
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
    public async Task A_delivered_page_publishes_the_despatch_first_and_stops_the_polling()
    {
        Shipment shipment = await fixture.BookedAsync("050000");

        (await Worker().ProcessBatchAsync(TestContext.Current.CancellationToken)).ShouldBe(1);

        (await fixture.StatusAsync(shipment.Id)).ShouldBe("Delivered");
        (await fixture.NextPollAtAsync(shipment.Id)).ShouldBeNull(
            "a terminal shipment has nothing further to learn");
        (await fixture.LockedUntilAsync(shipment.Id)).ShouldBeNull(
            "a terminal row is released like any other, not left to its lease");

        // Order, not membership: Ordering's saga finalises on the first and
        // ADR-051's projection reads both.
        (await fixture.OutboxAsync())
            .OrderBy(row => row.OccurredAt)
            .Select(row => row.MessageType.Split('.')[^1])
            .ShouldBe(["ShipmentDispatched", "ShipmentDelivered"]);
    }

    [Fact]
    public async Task A_refused_cancellation_goes_on_being_tracked_and_despatches_on_collection()
    {
        // Spec section 6's third case. SIM-LATE's cancel answers too late, and
        // its events fall to the simulator's default page, collected and then
        // delivered, so the despatch is published ahead of the delivery.
        Shipment shipment = await fixture.BookedAsync("SIM-LATE");
        await fixture.RequestCancellationAsync(shipment.Id);

        (await fixture.RunFulfilmentPassAsync()).ShouldBe(1);
        (await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM shipping.Shipments WHERE Id = {0} AND CancellationRefusedAt IS NOT NULL",
            shipment.Id.Value)).ShouldBe(1, "the carrier refused the cancellation");

        // Still Booked with both stamps, so only the refusal predicate keeps
        // the fulfilment claim off it. Carrier calls, not the pass's count: a
        // re-asked cancellation is refused again and moves nothing.
        int carrierCalls = fixture.Carrier.LogEntries.Count;
        await fixture.RunFulfilmentPassAsync();
        fixture.Carrier.LogEntries.Count.ShouldBe(carrierCalls, "a refused cancellation is outside the claim");

        (await Worker().ProcessBatchAsync(TestContext.Current.CancellationToken)).ShouldBe(1);

        (await fixture.StatusAsync(shipment.Id)).ShouldBe("Delivered");
        (await fixture.OutboxAsync())
            .OrderBy(row => row.OccurredAt)
            .Select(row => row.MessageType.Split('.')[^1])
            .ShouldBe(["ShipmentDispatched", "ShipmentDelivered"]);

        // Deliver raises the despatch itself on a Booked row, so the order
        // above holds without Collected; the instant is the simulator's
        // default page script (spec, section 9), and only Collected supplies it.
        (await fixture.OutboxAsync())
            .Single(row => row.MessageType.EndsWith("ShipmentDispatched", StringComparison.Ordinal))
            .OccurredAt.ShouldBe(new DateTimeOffset(2026, 1, 2, 9, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task A_row_still_being_polled_is_not_claimed_by_a_second_pass()
    {
        Shipment shipment = await fixture.BookedAsync("SIM-TRANSIT");

        // Staged, not two passes back to back: the claim's lease is what the
        // second pass must see, and a pass that has already committed would
        // prove nothing about a row in flight.
        await fixture.ClaimForTrackingAsync();

        (await Worker().ProcessBatchAsync(TestContext.Current.CancellationToken)).ShouldBe(0);

        // A pass that claimed the row and failed its poll also answers 0, and
        // leaves Attempts at 1: the row as booked is what says it was skipped.
        (await fixture.StatusAsync(shipment.Id)).ShouldBe("Booked");
        (await fixture.AttemptsAsync(shipment.Id)).ShouldBe(0);
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
        // The one row both claims select: a Booked shipment whose cancellation
        // the carrier has not answered is in FulfilmentClaims' second
        // population and due a poll (spec, section 4). The status filters
        // overlap by design; the LockedUntil predicate in TrackingClaims is
        // what keeps this worker off a row the other holds.
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
        // The row both claims select, held under this worker's lease: the
        // LockedUntil predicate in FulfilmentClaims is what refuses it (spec,
        // section 4). Driven through ServiceFixture.RunFulfilmentPassAsync, so
        // the claim that refuses is FulfilmentClaims' own and not a copy.
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
        // The claim itself failing — the database unreachable — is the case
        // ExecuteAsync's filter exists for. Driven through the loop and not
        // through ProcessBatchAsync, because what is under test is the catch
        // around the pass rather than the pass.
        using ShippingWorkerFactory broken = new(Unreachable.Sql, Unreachable.Rabbit);

        TrackingWorker worker = broken.Services.GetRequiredService<TrackingWorker>();

        // The pass itself throws, which is what the catch below is about and
        // what a carrier outage would never produce: that is caught per row.
        await Should.ThrowAsync<Exception>(
            () => worker.ProcessBatchAsync(TestContext.Current.CancellationToken));

        await worker.StartAsync(TestContext.Current.CancellationToken);

        // Staged on the loop's own line rather than on a sleep: PeriodicTimer
        // first fires one CarrierHop.TrackingTick after the start, inside the
        // wait's deadline, and the direct call above logs nothing. A loop that
        // let the fault out completes instead of logging.
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
