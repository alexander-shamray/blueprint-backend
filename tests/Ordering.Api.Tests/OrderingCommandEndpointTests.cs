using Common.Application;
using Common.Contracts.Inventory.V1;
using Common.Contracts.Ordering.V1;
using Common.Infrastructure.Inbox;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Application.Orders.FlagOrderForReview;
using Ordering.Infrastructure.Messaging;
using Ordering.TestSupport;
using Shouldly;
using Xunit;
// Aliased because Common.Application has a DependencyInjection too.
using MessagingRegistration = Ordering.Infrastructure.Messaging.DependencyInjection;

namespace Ordering.Api.Tests;

/// <summary><c>ordering-commands</c> (§9.4) and <c>ordering-stock-events</c> (§9.8), over the real broker.</summary>
/// <remarks>
/// The harness in <see cref="MessagingRegistrationTests"/> replaces the callback these endpoints live in, so
/// their bindings are asserted here over a real broker.
/// </remarks>
[Collection(nameof(IntegrationCollection))]
public sealed class OrderingCommandEndpointTests(ServiceFixture fixture) : IAsyncLifetime
{
    private static readonly Guid Customer = Guid.Parse("55555555-5555-5555-5555-555555555555");

    /// <summary>A broker round trip on a loaded runner, bounded since an unbound endpoint never arrives.</summary>
    private static readonly TimeSpan DeliveryBudget = TimeSpan.FromSeconds(30);

    /// <summary>Every delivery this test started, with its endpoint, since one event can reach two queues.</summary>
    private readonly List<(Guid MessageId, string Endpoint)> _published = [];

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    /// <summary>Drains this test's deliveries, so the next reset cannot truncate under a live consumer.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach ((Guid messageId, string endpoint) in _published)
        {
            await Eventually(
                async () => (await InboxRowsAsync(messageId, endpoint)).Count,
                expected: 1,
                because: "a delivery still running when the next test resets is a flake in that test");
        }
    }

    [Fact]
    public async Task A_reservation_from_Inventory_advances_the_order_over_the_broker()
    {
        // StockReserved through ordering-stock-events, StockReservedHandler and the dispatcher to Order.ConfirmStock.
        Guid orderId = await fixture.SeedOrderAsync(Customer);

        (await StatusAsync(orderId)).ShouldBe("AwaitingStock", "the arrange half is a claim too");

        await PublishStockReservedAsync(orderId, Guid.CreateVersion7());

        await EventuallyStatus(
            orderId,
            "AwaitingPayment",
            because: "the binding, the consumer, the handler's dispatch and the aggregate transition are " +
                "four links, and this is the only test that crosses all four");
    }

    [Fact]
    public async Task A_redelivered_reservation_is_suppressed_by_the_inbox()
    {
        // ConfirmStock is not idempotent, so the inbox must drop the redelivery; scoped to this endpoint, since the
        // same id also leaves a row on the saga's queue.
        Guid orderId = await fixture.SeedOrderAsync(Customer);
        var messageId = Guid.CreateVersion7();

        await PublishStockReservedAsync(orderId, messageId);
        await EventuallyStatus(orderId, "AwaitingPayment", because: "the first delivery must land");

        await Eventually(
            async () => (await InboxRowsAsync(messageId, MessagingRegistration.StockEventsQueue)).Count,
            expected: 1,
            because: "a delivery that leaves no inbox row reached an endpoint with no filter on it");

        IReadOnlyList<InboxMessage> rows = await InboxRowsAsync(messageId, MessagingRegistration.StockEventsQueue);
        rows[0].Endpoint.ShouldBe(
            MessagingRegistration.StockEventsQueue,
            "the saga binds the same event on its own queue — a row from that endpoint would mean this " +
            "consumer was bound there instead, under a retry policy written for a state machine");

        // The same id again. The row count, not the status, discriminates: a rejected duplicate leaves the same
        // status as a suppressed one, since StockReservedHandler drops the Result (§9.8).
        await PublishStockReservedAsync(orderId, messageId, drain: false);

        // A sentinel with a fresh id bounds the wait, and is drained on its own.
        Guid sentinelOrderId = await fixture.SeedOrderAsync(Customer);
        await PublishStockReservedAsync(sentinelOrderId, Guid.CreateVersion7());
        await EventuallyStatus(
            sentinelOrderId,
            "AwaitingPayment",
            because: "a message published after the duplicate has been consumed, so the endpoint has " +
                "had the duplicate in front of it — a wait that scales with the runner rather than a " +
                "fixed three seconds that is generous here and short on a loaded agent");

        (await StatusAsync(orderId)).ShouldBe("AwaitingPayment");
        (await InboxRowsAsync(messageId, MessagingRegistration.StockEventsQueue))
            .Count
            .ShouldBe(1, "a suppressed duplicate writes no second row");

        // The sentinel bounds this wait but proves no order: no ConcurrentMessageLimit is set, so it may finish first.
    }

    [Fact]
    public async Task A_confirmation_reaches_the_aggregate_through_the_command_queue()
    {
        // ordering-commands, its mapper and CommandConsumer, and ConfirmOrderHandler.
        Guid orderId = await fixture.SeedOrderAsync(Customer);

        await PublishStockReservedAsync(orderId, Guid.CreateVersion7());
        await EventuallyStatus(orderId, "AwaitingPayment", because: "ConfirmOrder needs a paid-for order");

        await SendAsync(new ConfirmOrder(orderId, "psp-endpoint-1"));

        await EventuallyStatus(
            orderId,
            "Confirmed",
            because: "the mapper parses the reference into PaymentReference and the handler applies it");

        // The published confirmation is the part that can be asserted: one Broker row for §3.2's OrderConfirmed.
        (await fixture.OutboxAsync())
            .Count(r => r.Lane == OutboxLane.Broker
                && r.MessageType.Contains("OrderConfirmed", StringComparison.Ordinal))
            .ShouldBe(1, "confirming stages §9.3's allow-listed contract on the Broker lane");

    }

    [Fact]
    public async Task A_despatch_marks_the_order_shipped_through_the_command_queue()
    {
        Guid orderId = await fixture.SeedOrderAsync(Customer);

        await PublishStockReservedAsync(orderId, Guid.CreateVersion7());
        await EventuallyStatus(orderId, "AwaitingPayment", because: "the arrange half");

        await SendAsync(new ConfirmOrder(orderId, "psp-endpoint-2"));
        await EventuallyStatus(orderId, "Confirmed", because: "MarkOrderShipped requires it");

        await SendAsync(new MarkOrderShipped(orderId, "TRACK-ENDPOINT-1"));

        await EventuallyStatus(orderId, "Shipped", because: "the last transition the saga asks for");
    }

    [Fact]
    public async Task An_escalation_writes_a_review_row_and_touches_no_aggregate()
    {
        // §9.6's one command that changes no business state: the row appears, and the order does not move.
        Guid orderId = await fixture.SeedOrderAsync(Customer);

        await SendAsync(new FlagOrderForReview(orderId, ReviewReasons.NotDespatched));

        await Eventually(
            () => fixture.ScalarAsync<int>(
                "SELECT Value = COUNT(*) FROM ordering.OrderReviews WHERE OrderId = {0} AND Reason = {1}",
                orderId,
                ReviewReasons.NotDespatched),
            expected: 1,
            because: "the handler writes through IUnitOfWork.ExecuteRawAsync inside the command's own " +
                "transaction (§9.6)");

        (await StatusAsync(orderId)).ShouldBe(
            "AwaitingStock",
            "an escalation is a fact about the process, not about the order");
    }

    [Fact]
    public async Task A_second_escalation_for_one_reason_is_absorbed_rather_than_duplicated()
    {
        // Two escalations for one reason are two sends with two ids, so the conditional insert, not §9.5's inbox,
        // absorbs the second (§9.6).
        Guid orderId = await fixture.SeedOrderAsync(Customer);

        await SendAsync(new FlagOrderForReview(orderId, ReviewReasons.StockNotReleased));
        await Eventually(
            () => ReviewCountAsync(orderId),
            expected: 1,
            because: "the first escalation must land before a second can be absorbed");

        await SendAsync(new FlagOrderForReview(orderId, ReviewReasons.StockNotReleased));
        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        (await ReviewCountAsync(orderId)).ShouldBe(
            1,
            "RaisedAt records when the process first stalled, so a repeat must not insert or move it");
    }

    [Fact]
    public async Task Two_escalations_racing_for_one_reason_both_succeed()
    {
        // The range lock shows only concurrently: without WITH (UPDLOCK, HOLDLOCK) the loser takes a primary key
        // violation. Dispatched in process, since the endpoint's §9.8 retry would absorb the fault.
        Guid orderId = await fixture.SeedOrderAsync(Customer);

        // A gate, so the racers' read-then-write windows overlap rather than serialising behind scope resolution.
        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task[] racers = [.. Enumerable.Range(0, 8).Select(_ => DispatchEscalationAsync(orderId, start.Task))];

        start.SetResult();

        await Should.NotThrowAsync(
            () => Task.WhenAll(racers),
            "a concurrent duplicate must be absorbed by the range lock, not surface as a primary key " +
            "violation — without WITH (UPDLOCK, HOLDLOCK) this throws " +
            "\"Violation of PRIMARY KEY constraint 'PK_OrderReviews'\"");

        (await ReviewCountAsync(orderId)).ShouldBe(
            1,
            "and the lock must absorb them rather than deadlock — eight writers, one row");
    }

    private async Task DispatchEscalationAsync(Guid orderId, Task gate)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();

        // Resolved before the gate, so what is released is the dispatches alone.
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        await gate;

        await dispatcher.SendAsync(
            new FlagOrderForReviewCommand(orderId, ReviewReasons.NotDespatched),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_command_this_service_cannot_parse_changes_nothing()
    {
        // §9.8's ContractMappingException path: the claim is only that the aggregate is untouched.
        Guid orderId = await fixture.SeedOrderAsync(Customer);

        // Undrained: it faults in the mapper, so the filter never writes its row.
        await SendAsync(new CancelOrder(orderId, "reason_from_a_newer_deployment"), drain: false);

        // A valid command for a second order bounds the wait; the unmappable one is not retried (§9.8).
        Guid sentinelOrderId = await fixture.SeedOrderAsync(Customer);
        await SendAsync(new CancelOrder(sentinelOrderId, CancelReasons.OutOfStock));
        await EventuallyStatus(
            sentinelOrderId,
            "Cancelled",
            because: "a command sent after the unmappable one has been handled, so the endpoint has " +
                "had the unmappable one in front of it");

        (await StatusAsync(orderId)).ShouldBe(
            "AwaitingStock",
            "an unmappable reason must not reach Order.Cancel with a defaulted value");
    }

    private Task<int> ReviewCountAsync(Guid orderId) =>
        fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM ordering.OrderReviews WHERE OrderId = {0}",
            orderId);

    private async Task<string> StatusAsync(Guid orderId) =>
        await fixture.ScalarAsync<string>(
            "SELECT Value = Status FROM ordering.Orders WHERE Id = {0}",
            orderId);

    private async Task EventuallyStatus(Guid orderId, string expected, string because)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + DeliveryBudget;
        string actual = "";

        while (DateTimeOffset.UtcNow < deadline)
        {
            actual = await StatusAsync(orderId);

            if (actual == expected)
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        }

        actual.ShouldBe(expected, because);
    }

    /// <summary>Publishes as Inventory would; <paramref name="drain"/> is false for a dropped duplicate.</summary>
    private async Task PublishStockReservedAsync(Guid orderId, Guid messageId, bool drain = true)
    {
        StockReserved reserved = new()
        {
            MessageId = messageId,
            CorrelationId = orderId,
            OccurredAt = DateTimeOffset.UtcNow,
            OrderId = orderId
        };

        if (drain)
        {
            // Both readers of StockReserved record it: the consumer's queue and the saga's.
            _published.Add((messageId, MessagingRegistration.StockEventsQueue));
            _published.Add((messageId, MessagingRegistration.FulfilmentSagaQueue));
        }

        await fixture.Factory.Services
            .GetRequiredService<IBus>()
            .Publish(
                reserved,
                c =>
                {
                    c.MessageId = messageId;
                    c.CorrelationId = reserved.CorrelationId;
                },
                TestContext.Current.CancellationToken);
    }

    /// <summary>Sends to <c>ordering-commands</c> by address, as §9.6's saga does.</summary>
    /// <param name="drain">False only where the mapper throws, so the inbox row is never written.</param>
    private async Task SendAsync<T>(T command, bool drain = true)
        where T : class
    {
        ISendEndpoint endpoint = await fixture.Factory.Services
            .GetRequiredService<IBus>()
            .GetSendEndpoint(new Uri($"queue:{MessagingRegistration.CommandsQueue}"));

        var messageId = Guid.CreateVersion7();

        if (drain)
            _published.Add((messageId, MessagingRegistration.CommandsQueue));

        await endpoint.Send(command, c => c.MessageId = messageId, TestContext.Current.CancellationToken);
    }

    private async Task<IReadOnlyList<InboxMessage>> InboxRowsAsync(Guid messageId, string endpoint) =>
        [.. (await fixture.InboxAsync()).Where(r => r.MessageId == messageId && r.Endpoint == endpoint)];

    private static async Task Eventually(Func<Task<int>> read, int expected, string because)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + DeliveryBudget;
        int actual = 0;

        while (DateTimeOffset.UtcNow < deadline)
        {
            actual = await read();

            if (actual == expected)
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        }

        actual.ShouldBe(expected, because);
    }
}
