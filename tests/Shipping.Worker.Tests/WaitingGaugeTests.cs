using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shipping.Domain.Shipments;
using Shipping.Infrastructure;
using Shipping.Infrastructure.Carrier;
using Shipping.Infrastructure.Observability;
using Shipping.TestSupport;
using Shouldly;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>
/// Spec section 11's third instrument: rows past their first backoff, by state.
/// Delivery lag stops when a consumer starts, so it never sees a worker waiting
/// on a carrier — this gauge is the only signal that does.
/// </summary>
[Collection(nameof(IntegrationCollection))]
public sealed class WaitingGaugeTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_row_past_its_first_backoff_is_counted_under_its_own_state()
    {
        Shipment booked = await fixture.BookedAsync("SIM-TRANSIT");
        await fixture.SetAttemptsAsync(booked.Id, 1);

        // A second Booked row with no failed pass: Booked reading 1 with two
        // Booked rows present is what shows the predicate excludes it.
        await fixture.BookedAsync("050000");

        List<(string State, double Value)> measured = ReadWaitingGauge();

        measured.ShouldContain(m => m.State == "Booked" && m.Value == 1);
        measured.ShouldContain(m => m.State == "Pending" && m.Value == 0);
        measured.Select(m => m.State).ShouldBe(
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

        List<(string State, double Value)> measured = ReadWaitingGauge();

        measured.ShouldContain(m => m.State == "Booked" && m.Value == 1);
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

        List<(string State, double Value)> measured = ReadWaitingGauge();

        measured.ShouldContain(
            m => m.State == "Voided" && m.Value == 0,
            "a row no worker will claim again is not waiting on anything");
    }

    /// <summary>
    /// Spec section 11's waiting gauge, read once per call: one entry per state
    /// the callback reported, with the value it produced. Built over a stats
    /// reader of this suite's own rather than the host's, whose per-state cache
    /// the host's metric reader can fill from an empty table between the
    /// booking and this read; the claim here is the gauge's shape, its tag and
    /// its predicate. The filter is on the meter instance and never its name,
    /// and the instrument name is written out rather than taken from the
    /// registration, which would agree with itself whatever it is called.
    /// </summary>
    private List<(string State, double Value)> ReadWaitingGauge()
    {
        // The factory has to outlive the collection: a Meter disposed with its
        // factory publishes nothing, and DefaultMeterFactory is internal to
        // Microsoft.Extensions.Diagnostics, so a container is how a test holds
        // one at all.
        using IMeterFactory factory = new ServiceCollection()
            .AddMetrics()
            .BuildServiceProvider()
            .GetRequiredService<IMeterFactory>();

        using ShipmentStats stats = new(new SqlConnectionFactory(fixture.ConnectionString));
        ShipmentMetrics metrics = new(factory, stats, NullLogger<ShipmentMetrics>.Instance);
        metrics.ShouldNotBeNull();

        // The same Meter the constructor above used — IMeterFactory caches by
        // name, so this is a handle on it rather than a second meter.
        Meter mine = factory.Create(CarrierMetrics.MeterName);

        List<(string State, double Value)> measured = [];
        using MeterListener listener = new();

        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, mine) && instrument.Name == "shipping.shipments.waiting")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>(
            (_, value, tags, _) => measured.Add((StateOf(tags), value)));

        listener.Start();
        listener.RecordObservableInstruments();

        // Fails closed: with nothing enabled the caller's ShouldContain would
        // be asserting over an empty list rather than over a reading.
        measured.ShouldNotBeEmpty("the listener enabled none of this meter's instruments");

        return measured;
    }

    /// <summary>
    /// The <c>state</c> tag a measurement carries, or the empty string where it
    /// carries none — which no assertion matches, so a tag renamed fails the
    /// assertion that reads it rather than being silently dropped.
    /// </summary>
    private static string StateOf(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            if (tag.Key == "state")
                return tag.Value?.ToString() ?? "";
        }

        return "";
    }
}
