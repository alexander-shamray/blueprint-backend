using System.Collections.Concurrent;
using System.Diagnostics;
using Common.Infrastructure.Tracing;
using Notifications.Application.Records;
using Notifications.Infrastructure.Delivery;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>
/// The hop through <c>NotificationLog</c> on a real broker and relay (§9.4): the trace that delivered the event a
/// notice was owed for is the one its send pass runs in.
/// </summary>
[Collection(nameof(IntegrationCollection))]
public sealed class SendTraceContextTests(ServiceFixture fixture) : IAsyncLifetime
{
    private const string Mailbox = "aigerim@example.test";

    private static readonly DateTimeOffset At = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private readonly ConcurrentQueue<Activity> _stopped = new();

    private ActivityListener? _listener;

    public async ValueTask InitializeAsync()
    {
        await fixture.ResetWithRelayAsync();

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
    public async Task The_send_pass_is_a_child_of_the_trace_that_delivered_the_event()
    {
        (Guid order, Guid customer) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        fixture.ContactAnswers(customer, Mailbox, "en");
        ActivityTraceId placing;
        using ActivitySource requests = new("Request");

        using (Activity request = requests.StartActivity("request").ShouldNotBeNull())
        {
            placing = request.TraceId;
            await fixture.DeliverAsync(OrderEvents.Placed(order, customer, At));
        }

        await fixture.WaitUntilDueAsync(order);
        (await fixture.RunSendPassAsync()).ShouldBe(new SendPass(1, 1));

        Notification sent = (await fixture.NotificationsAsync(order)).ShouldHaveSingleItem();
        Activity pass = PassOf(sent.NotificationId);
        pass.TraceId.ShouldBe(placing);
        pass.ParentSpanId.ShouldNotBe(default, "the pass is restored as a child, not started as a root");
    }

    [Fact]
    public async Task A_row_written_with_no_trace_is_sent_in_a_trace_of_its_own()
    {
        // A row the version without the columns wrote reads back null, and is sent as it always was (§7.4).
        (Guid order, Guid customer) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        fixture.ContactAnswers(customer, Mailbox, "en");
        await fixture.DeliverAsync(OrderEvents.Placed(order, customer, At));
        await fixture.WaitUntilDueAsync(order);
        await fixture.ExecuteAsync(
            "UPDATE notifications.NotificationLog SET TraceParent = NULL, TraceState = NULL WHERE OrderId = {0};",
            order);

        (await fixture.RunSendPassAsync()).ShouldBe(new SendPass(1, 1));

        Notification sent = (await fixture.NotificationsAsync(order)).ShouldHaveSingleItem();
        PassOf(sent.NotificationId).ParentSpanId.ShouldBe(default);
    }

    /// <summary>By the notification's id, since the listener hears every claimed pass in the process.</summary>
    private Activity PassOf(Guid notification) =>
        _stopped.Single(a =>
            a.Source.Name == StagedTrace.ClaimSourceName &&
            Equals(a.GetTagItem("notifications.notification.id"), notification));
}
