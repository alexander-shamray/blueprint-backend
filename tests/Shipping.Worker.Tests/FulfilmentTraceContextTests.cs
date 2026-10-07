using System.Collections.Concurrent;
using System.Diagnostics;
using Common.Infrastructure.Tracing;
using Shipping.Domain.Shipments;
using Shipping.TestSupport;
using Shouldly;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>
/// The hop through the shipments table on a real broker (§9.4): the trace that records a shipment, or asks for its
/// cancellation, is the one the fulfilment pass runs in, and each tracking poll links to it.
/// </summary>
[Collection(nameof(IntegrationCollection))]
public sealed class FulfilmentTraceContextTests(ServiceFixture fixture) : IAsyncLifetime
{
    private readonly FulfilmentSteps _steps = new(fixture);

    private readonly ConcurrentQueue<Activity> _stopped = new();

    private ActivityListener? _listener;

    public async ValueTask InitializeAsync()
    {
        await fixture.ResetAsync();

        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is "Request" or "MassTransit" or StagedTrace.ClaimSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _stopped.Enqueue
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public ValueTask DisposeAsync()
    {
        _listener?.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task The_booking_pass_is_a_child_of_the_trace_that_confirmed_the_order()
    {
        (Guid order, ActivityTraceId confirming) = await InTraceAsync(() => _steps.ConfirmAsync(FulfilmentSteps.Kazakh));

        (await fixture.RunFulfilmentPassAsync()).ShouldBe(1);
        (await _steps.StatusAsync(order)).ShouldBe("Booked");

        Activity pass = PassOf("shipment fulfil", await _steps.ShipmentIdAsync(order));
        pass.TraceId.ShouldBe(confirming);
        pass.ParentSpanId.ShouldNotBe(default, "the pass is restored as a child, not started as a root");
    }

    [Fact]
    public async Task The_cancellation_pass_joins_the_trace_that_asked_for_it_and_not_the_booking_s()
    {
        (Guid order, ActivityTraceId confirming) = await InTraceAsync(() => _steps.ConfirmAsync(FulfilmentSteps.Kazakh));
        await fixture.RunFulfilmentPassAsync();

        (_, ActivityTraceId cancelling) = await InTraceAsync(
            async () =>
            {
                await _steps.PublishAsync(FulfilmentSteps.Cancelled(order));
                return order;
            });
        cancelling.ShouldNotBe(confirming);
        _stopped.Clear();

        await fixture.RunFulfilmentPassAsync();

        (await _steps.StatusAsync(order)).ShouldBe("Voided");
        PassOf("shipment fulfil", await _steps.ShipmentIdAsync(order)).TraceId.ShouldBe(cancelling);
    }

    [Fact]
    public async Task A_tracking_poll_links_to_the_confirming_trace_and_carries_the_despatch_onward()
    {
        (Shipment shipment, ActivityTraceId confirming) = await InTraceAsync(() => fixture.BookedAsync("SIM-TRANSIT"));

        (await fixture.RunTrackingPassAsync()).ShouldBe(1);
        (await fixture.StatusAsync(shipment.Id)).ShouldBe("Dispatched");

        // A trace of its own, so a shipment polled for days does not stretch the order's, joined by its link.
        Activity poll = PassOf("shipment track", shipment.Id.Value);
        poll.TraceId.ShouldNotBe(confirming);
        poll.Links.ShouldContain(link => link.Context.TraceId == confirming);

        // The despatch is staged under the poll, so the outbox carries the poll's trace to Ordering and on (§9.4).
        (await fixture.OutboxAsync())
            .Single(row => row.MessageType.EndsWith("ShipmentDispatched", StringComparison.Ordinal))
            .TraceParent.ShouldNotBeNull()
            .ShouldContain(poll.TraceId.ToHexString());
    }

    [Fact]
    public async Task A_row_written_with_no_trace_is_fulfilled_in_a_trace_of_its_own()
    {
        // A row the version without the columns recorded reads back null, and is fulfilled as it always was (§7.4).
        Guid order = await _steps.ConfirmAsync(FulfilmentSteps.Kazakh);
        await fixture.ExecuteAsync(
            "UPDATE shipping.Shipments SET TraceParent = NULL, TraceState = NULL WHERE OrderId = {0};",
            order);

        (await fixture.RunFulfilmentPassAsync()).ShouldBe(1);

        PassOf("shipment fulfil", await _steps.ShipmentIdAsync(order)).ParentSpanId.ShouldBe(default);
    }

    /// <summary>Runs a step inside a request span, as the gateway's would be, and returns that span's trace.</summary>
    private static async Task<(T Result, ActivityTraceId Trace)> InTraceAsync<T>(Func<Task<T>> step)
    {
        using ActivitySource requests = new("Request");
        using Activity request = requests.StartActivity("request").ShouldNotBeNull();

        T result = await step();

        return (result, request.TraceId);
    }

    /// <summary>By name and shipment id, since the listener hears every claimed pass in the process.</summary>
    private Activity PassOf(string name, Guid shipment) =>
        _stopped.Single(a =>
            a.Source.Name == StagedTrace.ClaimSourceName &&
            a.OperationName == name &&
            Equals(a.GetTagItem("shipping.shipment.id"), shipment));
}
