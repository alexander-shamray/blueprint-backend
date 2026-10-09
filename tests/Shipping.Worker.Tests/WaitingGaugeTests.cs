using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shipping.Domain.Shipments;
using Shipping.Infrastructure;
using Shipping.Infrastructure.Carrier;
using Shipping.Infrastructure.Observability;
using Shipping.Infrastructure.Tracking;
using Shipping.TestSupport;
using Shouldly;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>Rows past their first backoff by state, the longest-due row's wait by pass, and unscanned rows.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class WaitingGaugeTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_row_past_its_first_backoff_is_counted_under_its_own_state()
    {
        // A Booked row with no failed pass, so Booked reading 1 shows the predicate excludes it; booked first, as the
        // other row's cancellation would be the next pass's claim.
        await fixture.BookedAsync("050000");

        // Awaiting its cancellation's answer, so the fulfilment claim still
        // selects it and its count is one a later pass will clear.
        Shipment booked = await fixture.BookedAsync("SIM-TRANSIT");
        await fixture.RequestCancellationAsync(booked.Id);
        await fixture.SetAttemptsAsync(booked.Id, 1);

        List<(string Tag, double Value)> measured = ReadGauge("shipping.shipments.waiting", "state");

        measured.ShouldContain(m => m.Tag == "Booked" && m.Value == 1);
        measured.ShouldContain(m => m.Tag == "Pending" && m.Value == 0);
        measured.Select(m => m.Tag).ShouldBe(
            Enum.GetNames<ShipmentStatus>(),
            ignoreOrder: true,
            "every state reports, because a state missing from a sum reads as a healthy zero");
    }

    [Fact]
    public async Task A_row_whose_polls_are_failing_is_counted_too()
    {
        // Each worker counts its own failures (ADR-054), so the gauge reads
        // both counts or it goes blind to a carrier whose feed is down.
        Shipment booked = await fixture.BookedAsync("SIM-TRANSIT");
        await fixture.SetPollAttemptsAsync(booked.Id, 1);

        List<(string Tag, double Value)> measured = ReadGauge("shipping.shipments.waiting", "state");

        measured.ShouldContain(m => m.Tag == "Booked" && m.Value == 1);
    }

    [Fact]
    public async Task A_fulfilment_count_no_claim_will_act_on_is_not_counted()
    {
        // A cancel that failed and then a collection: the row is Dispatched,
        // out of the fulfilment claim, and the count stays until it ends.
        Shipment dispatched = await fixture.BookedAsync("SIM-TRANSIT");
        (await fixture.RunTrackingPassAsync()).ShouldBe(1);
        (await fixture.StatusAsync(dispatched.Id)).ShouldBe("Dispatched");
        await fixture.RequestCancellationAsync(dispatched.Id);
        await fixture.SetAttemptsAsync(dispatched.Id, 1);

        List<(string Tag, double Value)> measured = ReadGauge("shipping.shipments.waiting", "state");

        measured.ShouldContain(
            m => m.Tag == "Dispatched" && m.Value == 0,
            "only the tracking pass will claim this row again, and its own count is clear");
    }

    [Fact]
    public async Task A_terminal_row_that_backed_off_is_not_counted()
    {
        // A pending row past its first backoff, then voided by OrderCancelled
        // through the real consumer: Shipment.Cancel moves the state and
        // leaves Attempts where the failed pass put it.
        FulfilmentSteps steps = new(fixture);
        Guid order = await steps.ConfirmAsync(FulfilmentSteps.Kazakh);
        ShipmentId pending = new(await steps.ShipmentIdAsync(order));
        await fixture.SetAttemptsAsync(pending, 1);

        await steps.PublishAsync(FulfilmentSteps.Cancelled(order));

        (await fixture.StatusAsync(pending)).ShouldBe("Voided");
        (await fixture.AttemptsAsync(pending)).ShouldBe(1, "the void left the counter, which is the case under test");

        List<(string Tag, double Value)> measured = ReadGauge("shipping.shipments.waiting", "state");

        measured.ShouldContain(
            m => m.Tag == "Voided" && m.Value == 0,
            "a row no worker will claim again is not waiting on anything");
    }

    [Fact]
    public async Task A_due_row_no_pass_holds_reads_as_its_wait_under_its_own_pass()
    {
        // A minute past due and held by nothing; with no cancellation, the fulfilment claim would take nothing.
        Shipment booked = await fixture.BookedAsync("SIM-TRANSIT");
        await fixture.ExecuteAsync(
            "UPDATE shipping.Shipments SET NextPollAt = DATEADD(second, -60, SYSDATETIMEOFFSET()) WHERE Id = {0};",
            booked.Id.Value);

        List<(string Tag, double Value)> measured = ReadGauge("shipping.shipments.overdue", "pass");

        measured.Single(m => m.Tag == "tracking").Value.ShouldBeInRange(60, 120);
        measured.Single(m => m.Tag == "fulfilment").Value.ShouldBe(0);
    }

    [Fact]
    public async Task A_row_a_pass_holds_has_no_wait()
    {
        // Due, and being worked: a MIN over the due column alone would read it.
        Shipment held = await fixture.BookedAsync("SIM-TRANSIT");
        await fixture.ClaimForTrackingAsync();

        (await fixture.LockedUntilAsync(held.Id)).ShouldNotBeNull();

        List<(string Tag, double Value)> measured = ReadGauge("shipping.shipments.overdue", "pass");

        measured.ShouldContain(m => m.Tag == "tracking" && m.Value == 0);
    }

    [Fact]
    public async Task A_pending_row_no_pass_has_reached_reads_under_fulfilment()
    {
        // Never attempted, and due.
        FulfilmentSteps steps = new(fixture);
        Guid order = await steps.ConfirmAsync(FulfilmentSteps.Kazakh);
        ShipmentId pending = new(await steps.ShipmentIdAsync(order));
        await fixture.ExecuteAsync(
            "UPDATE shipping.Shipments SET NextAttemptAt = DATEADD(second, -120, SYSDATETIMEOFFSET()) WHERE Id = {0};",
            pending.Value);

        List<(string Tag, double Value)> measured = ReadGauge("shipping.shipments.overdue", "pass");

        measured.Single(m => m.Tag == "fulfilment").Value.ShouldBeInRange(120, 180);
    }

    [Fact]
    public async Task A_booked_row_waits_from_its_cancellation_not_from_its_making()
    {
        // Booked two days ago, so NextAttemptAt holds the making; the cancellation thirty seconds ago starts the wait.
        Shipment booked = await fixture.BookedAsync("SIM-TRANSIT");
        await fixture.ExecuteAsync(
            "UPDATE shipping.Shipments SET NextAttemptAt = DATEADD(day, -2, SYSDATETIMEOFFSET()), " +
            "CancellationRequestedAt = DATEADD(second, -30, SYSDATETIMEOFFSET()) WHERE Id = {0};",
            booked.Id.Value);

        List<(string Tag, double Value)> measured = ReadGauge("shipping.shipments.overdue", "pass");

        measured.Single(m => m.Tag == "fulfilment").Value.ShouldBeInRange(30, 90);
    }

    [Fact]
    public async Task A_booking_never_scanned_past_its_first_scan_age_is_counted()
    {
        // Never polled, so neither has a scan; only the age tells them apart.
        Shipment old = await fixture.BookedAsync("SIM-TRANSIT");
        Shipment recent = await fixture.BookedAsync("SIM-TRANSIT");
        await fixture.AgeCreatedAsync(old.Id, ShipmentStats.FirstScanAge + TimeSpan.FromHours(1));
        await fixture.AgeCreatedAsync(recent.Id, ShipmentStats.FirstScanAge - TimeSpan.FromHours(1));

        ReadUnscanned().ShouldBe(1);
    }

    [Fact]
    public async Task A_booking_the_carrier_has_scanned_is_not_counted()
    {
        // Collected and so Dispatched, and a scan that leaves the row Booked: the carrier knows both.
        Shipment dispatched = await fixture.BookedAsync("SIM-TRANSIT");
        (await fixture.RunTrackingPassAsync()).ShouldBe(1);
        Shipment scanned = await fixture.BookedAsync("SIM-TRANSIT");
        await fixture.ExecuteAsync(
            """
            INSERT INTO shipping.TrackingEvents (ShipmentId, CarrierEventId, Status, OccurredAt, RecordedAt)
            VALUES ({0}, 'evt-in-transit', 'InTransit', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());
            """,
            scanned.Id.Value);
        await fixture.AgeCreatedAsync(dispatched.Id, ShipmentStats.FirstScanAge + TimeSpan.FromHours(1));
        await fixture.AgeCreatedAsync(scanned.Id, ShipmentStats.FirstScanAge + TimeSpan.FromHours(1));

        (await fixture.StatusAsync(dispatched.Id)).ShouldBe("Dispatched");
        (await fixture.StatusAsync(scanned.Id)).ShouldBe("Booked");
        ReadUnscanned().ShouldBe(0);
    }

    [Fact]
    public async Task A_booking_whose_cancellation_was_asked_is_not_counted()
    {
        Shipment held = await fixture.BookedAsync("SIM-TRANSIT");
        await fixture.RequestCancellationAsync(held.Id);
        await fixture.AgeCreatedAsync(held.Id, ShipmentStats.FirstScanAge + TimeSpan.FromHours(1));

        ReadUnscanned().ShouldBe(0, "a parcel held back for the carrier's answer is not one it should have collected");
    }

    [Fact]
    public async Task A_booking_whose_cancellation_the_carrier_refused_is_counted()
    {
        // Too late to cancel, so the parcel is moving and the row stays Booked and polled.
        Shipment refused = await fixture.BookedAsync("SIM-LATE");
        await fixture.RequestCancellationAsync(refused.Id);
        (await fixture.RunFulfilmentPassAsync()).ShouldBe(1);
        await fixture.AgeCreatedAsync(refused.Id, ShipmentStats.FirstScanAge + TimeSpan.FromHours(1));

        (await fixture.StatusAsync(refused.Id)).ShouldBe("Booked");
        (await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM shipping.Shipments WHERE Id = {0} AND CancellationRefusedAt IS NOT NULL",
            refused.Id.Value)).ShouldBe(1);
        ReadUnscanned().ShouldBe(1, "a refused cancellation leaves a parcel the carrier should have scanned");
    }

    [Fact]
    public async Task A_shipment_that_has_ended_is_not_counted()
    {
        // Abandoned without asking the carrier, and voided by it, so neither ever had a scan.
        Shipment delivered = await fixture.DeliveredAsync();
        Shipment voided = await fixture.BookedAsync("SIM-TRANSIT");
        await fixture.RequestCancellationAsync(voided.Id);
        (await fixture.RunFulfilmentPassAsync()).ShouldBe(1);
        Shipment abandoned = await fixture.BookedAsync("SIM-TRANSIT");
        await fixture.AgeCreatedAsync(abandoned.Id, TrackingWorker.GiveUpAge + TimeSpan.FromHours(1));
        (await fixture.RunTrackingPassAsync()).ShouldBe(1);
        await fixture.AgeCreatedAsync(delivered.Id, ShipmentStats.FirstScanAge + TimeSpan.FromHours(1));
        await fixture.AgeCreatedAsync(voided.Id, ShipmentStats.FirstScanAge + TimeSpan.FromHours(1));

        (await fixture.StatusAsync(voided.Id)).ShouldBe("Voided");
        (await fixture.StatusAsync(abandoned.Id)).ShouldBe("Abandoned");
        ReadUnscanned().ShouldBe(0);
    }

    private double ReadUnscanned() => ReadGauge("shipping.shipments.unscanned", tagKey: "").Single().Value;

    /// <summary>One of <see cref="ShipmentMetrics"/>' gauges, read once over this suite's own stats reader.</summary>
    private List<(string Tag, double Value)> ReadGauge(string instrumentName, string tagKey)
    {
        // The factory has to outlive the collection: a Meter disposed with its factory publishes nothing.
        using IMeterFactory factory = new ServiceCollection()
            .AddMetrics()
            .BuildServiceProvider()
            .GetRequiredService<IMeterFactory>();

        using ShipmentStats stats = new(new SqlConnectionFactory(fixture.ConnectionString));
        ShipmentMetrics metrics = new(factory, stats, NullLogger<ShipmentMetrics>.Instance);
        metrics.ShouldNotBeNull();

        // The same Meter the constructor used, since IMeterFactory caches by name.
        Meter mine = factory.Create(CarrierMetrics.MeterName);

        List<(string Tag, double Value)> measured = [];
        using MeterListener listener = new();

        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, mine) && instrument.Name == instrumentName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>(
            (_, value, tags, _) => measured.Add((TagOf(tags, tagKey), value)));

        listener.Start();
        listener.RecordObservableInstruments();

        // Fails closed: with nothing enabled the caller's ShouldContain would
        // be asserting over an empty list rather than over a reading.
        measured.ShouldNotBeEmpty("the listener enabled none of this meter's instruments");

        return measured;
    }

    /// <summary>The tag under <paramref name="key"/>, or an empty string no assertion matches.</summary>
    private static string TagOf(ReadOnlySpan<KeyValuePair<string, object?>> tags, string key)
    {
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            if (tag.Key == key)
                return tag.Value?.ToString() ?? "";
        }

        return "";
    }
}
