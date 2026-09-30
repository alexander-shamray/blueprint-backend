using Common.Contracts.Inventory.V1;
using Common.Contracts.Ordering.V1;
using Common.Infrastructure.Inbox;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Infrastructure.Messaging;
using Ordering.TestSupport;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>The saga's EF repository and endpoint against real SQL Server and a real broker (§9.6).</summary>
/// <remarks>§12.5's harness replaces the repository and transport with doubles, so these halves live here.</remarks>
[Collection(nameof(IntegrationCollection))]
public sealed class OrderFulfilmentSagaEndpointTests(ServiceFixture fixture) : IAsyncLifetime
{
    private static readonly Guid Customer = Guid.Parse("66666666-6666-6666-6666-666666666666");

    private static readonly TimeSpan DeliveryBudget = TimeSpan.FromSeconds(30);

    /// <summary>Every message this test published, so the teardown can wait for its inbox rows.</summary>
    private readonly List<(Guid MessageId, string Endpoint)> _published = [];

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    /// <summary>
    /// Waits for each delivery's inbox row, written after the consumer returns (§9.5), so the next reset
    /// cannot truncate under a commit still in flight.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        foreach ((Guid messageId, string endpoint) in _published)
        {
            await Eventually(
                async () => (await InboxRowsAsync(messageId, endpoint)).Count,
                expected: 1,
                because: $"a delivery still running when the next test resets is a flake in that test " +
                    $"— {endpoint}, for message {messageId}");
        }
    }

    [Fact]
    public async Task The_instance_is_persisted_across_a_transition_and_deleted_on_finalise()
    {
        var orderId = Guid.CreateVersion7();

        await PublishPlacedAsync(orderId, Guid.CreateVersion7());

        await Eventually(
            () => SagaRowsAsync(orderId),
            expected: 1,
            because: "AddSagaStateMachine's EntityFrameworkRepository is what writes this row, and the " +
                "harness suite replaces it with an in-memory double");

        (await fixture.ScalarAsync<string>(
            "SELECT Value = CurrentState FROM ordering.OrderFulfilmentStates WHERE CorrelationId = {0}",
            orderId))
            .ShouldBe("AwaitingStock", "the state column is the mapping under test, not the transition");

        // A failure that finalises immediately, so the delete is observable without waiting out a timeout.
        await PublishReservationFailedAsync(orderId, Guid.CreateVersion7());

        await Eventually(
            () => SagaRowsAsync(orderId),
            expected: 0,
            because: "SetCompletedWhenFinalized deletes the instance, which is why §9.6's diagram has no " +
                "Cancelled state — and nothing else in the suite watches that it really does");
    }

    [Fact]
    public async Task A_cancellation_that_overtakes_its_placement_is_rescued_by_the_retry()
    {
        // §9.8's retries give the placement time to land, which §12.5's harness cannot show. The delay is not
        // a proof: on a loaded runner the placement can win, and this then passes on the ordinary path.
        var orderId = Guid.CreateVersion7();

        await PublishCancelledAsync(orderId, Guid.CreateVersion7(), CancelOrigins.User);

        await Task.Delay(TimeSpan.FromSeconds(1.5), TestContext.Current.CancellationToken);

        (await SagaRowsAsync(orderId)).ShouldBe(
            0,
            "nothing has created an instance yet, so the delivery that has " +
                "already run found none");

        await PublishPlacedAsync(orderId, Guid.CreateVersion7());

        // The row first, since ScalarAsync throws on an empty result rather than answering.
        await Eventually(
            () => SagaRowsAsync(orderId),
            expected: 1,
            because: "the placement creates the instance the retry needs");

        await Eventually(
            () => fixture.ScalarAsync<string>(
                "SELECT Value = CurrentState FROM ordering.OrderFulfilmentStates " +
                "WHERE CorrelationId = {0}",
                orderId),
            expected: "Compensating",
            because: "a retried delivery has to correlate to the instance the " +
                "placement created and compensate; reaching the error queue " +
                "instead is the outcome #123 chose faulting to avoid");
    }

    [Fact]
    public async Task An_observed_cancellation_is_persisted_and_withholds_the_authorisation()
    {
        // The flag is written by one delivery and read by a later one, across the EF mapping §12.5's double replaces.
        var orderId = Guid.CreateVersion7();

        await PublishPlacedAsync(orderId, Guid.CreateVersion7());

        await Eventually(
            () => SagaRowsAsync(orderId),
            expected: 1,
            because: "the arrange half");

        // Inventory releasing off an OrderCancelled this saga has not consumed (ADR-029).
        await PublishReleasedAsync(orderId, Guid.CreateVersion7());

        await Eventually(
            () => fixture.ScalarAsync<int>(
                "SELECT Value = CAST(CancellationObserved AS int) " +
                "FROM ordering.OrderFulfilmentStates WHERE CorrelationId = {0}",
                orderId),
            expected: 1,
            because: "the recording branch has to reach the COLUMN, which is the " +
                "half a saga harness with an in-memory repository cannot prove");

        var reservedId = Guid.CreateVersion7();
        await PublishReservedAsync(orderId, reservedId);

        // Fenced on the inbox row, written after the consumer returns (§9.5), since the state already reads
        // AwaitingStock before the delivery.
        await Eventually(
            async () => (await SagaInboxRowsAsync(reservedId)).Count,
            expected: 1,
            because: "the state below is only evidence once this delivery has been " +
                "handled");

        (await fixture.ScalarAsync<string>(
            "SELECT Value = CurrentState FROM ordering.OrderFulfilmentStates " +
            "WHERE CorrelationId = {0}",
            orderId))
            .ShouldBe(
                "AwaitingStock",
                "an unguarded StockReserved sends AuthorisePayment and moves to " +
                "AwaitingPayment; the state column is where withholding is visible " +
                "without a harness to count sends with");
    }

    [Fact]
    public async Task A_row_this_build_writes_defaults_the_retained_CustomerId_to_empty()
    {
        // ADR-028's expand half: the instance does not write CustomerId, and §15.5's previous release may read
        // it, where Guid.Empty names nobody.
        var orderId = Guid.CreateVersion7();

        await PublishPlacedAsync(orderId, Guid.CreateVersion7());

        await Eventually(
            () => SagaRowsAsync(orderId),
            expected: 1,
            because: "the row has to exist before its column values mean anything");

        (await fixture.ScalarAsync<Guid>(
            "SELECT Value = CustomerId FROM ordering.OrderFulfilmentStates WHERE CorrelationId = {0}",
            orderId))
            .ShouldBe(
                Guid.Empty,
                "the instance does not write this column, so the database default is the only thing " +
                "that can have supplied it — and empty is what makes an old build's read name nobody");
    }

    [Fact]
    public async Task The_retained_CustomerId_column_carries_a_default_constraint()
    {
        // The mechanism, asserted apart because a row could read empty because something wrote Guid.Empty.
        (await fixture.ScalarAsync<int>(
            """
            SELECT Value = COUNT(*)
            FROM sys.default_constraints d
            INNER JOIN sys.columns c
                ON c.object_id = d.parent_object_id
                AND c.column_id = d.parent_column_id
            WHERE d.parent_object_id = OBJECT_ID('ordering.OrderFulfilmentStates')
                AND c.name = 'CustomerId'
            """))
            .ShouldBe(
                1,
                "§15.5 forbids dropping a column the previous release still writes, so the column " +
                "survives this release with a default that lets this build's INSERT omit it");
    }

    [Fact]
    public async Task A_replayed_OrderPlaced_does_not_restart_a_finished_saga()
    {
        // A finalised saga has no row, so an at-least-once replay (§9.4) of the initial event would start it again.
        var orderId = Guid.CreateVersion7();
        var messageId = Guid.CreateVersion7();

        await PublishPlacedAsync(orderId, messageId);
        await Eventually(() => SagaRowsAsync(orderId), expected: 1, because: "the arrange half");

        await PublishReservationFailedAsync(orderId, Guid.CreateVersion7());
        await Eventually(() => SagaRowsAsync(orderId), expected: 0, because: "the saga must finish first");

        // The same message, again, which is what the outbox does after a crash.
        await PublishPlacedAsync(orderId, messageId);

        // A sentinel with a fresh id, so the wait is on a row rather than a clock and the teardown drains it.
        var sentinelOrderId = Guid.CreateVersion7();
        await PublishPlacedAsync(sentinelOrderId, Guid.CreateVersion7());
        await Eventually(
            () => SagaRowsAsync(sentinelOrderId),
            expected: 1,
            because: "a message published after the replay has been consumed, so the endpoint has had " +
                "the replay in front of it — five seconds was generous on this machine and a guess " +
                "about every other one");

        (await SagaRowsAsync(orderId)).ShouldBe(
            0,
            "a replayed OrderPlaced must not start fulfilment again — a second ReserveStock and a second " +
            "AuthorisePayment for one order is a double charge, and the row is the observable half of it");

        // A bound, not a proof of ordering: nothing pins this endpoint to one message at a time.
    }

    [Fact]
    public async Task A_scheduled_expiry_survives_the_endpoints_inbox_filter()
    {
        // The filter throws on a message with no MessageId, and an expiry comes from the scheduler, not a mapper.
        var orderId = Guid.CreateVersion7();

        await PublishPlacedAsync(orderId, Guid.CreateVersion7());
        await Eventually(() => SagaRowsAsync(orderId), expected: 1, because: "the arrange half");

        await PublishExpiredAsync(orderId, Guid.CreateVersion7());

        await Eventually(
            () => SagaRowsAsync(orderId),
            expected: 0,
            because: "the stock timeout cancels the order and finalises — if the filter had rejected the " +
                "message for want of a MessageId, the row would still be there");
    }

    [Fact]
    public async Task Two_events_for_one_instance_arriving_together_are_both_consumed()
    {
        // Two events in flight together; this does not pin ConcurrencyMode.Pessimistic, since the endpoint may
        // drain them back to back.
        var orderId = Guid.CreateVersion7();

        await PublishPlacedAsync(orderId, Guid.CreateVersion7());
        await Eventually(() => SagaRowsAsync(orderId), expected: 1, because: "the arrange half");

        Guid reservedId = Guid.CreateVersion7();
        Guid expiredId = Guid.CreateVersion7();

        await Task.WhenAll(
            PublishReservedAsync(orderId, reservedId),
            PublishExpiredAsync(orderId, expiredId));

        // InboxFilter writes its row only after the consumer returns, so a faulted consume leaves none.
        await Eventually(
            async () => (await SagaInboxRowsAsync(reservedId)).Count + (await SagaInboxRowsAsync(expiredId)).Count,
            expected: 2,
            because: "a row is written only once its consumer returns, so a transition that faulted on " +
                "the instance the other one changed leaves this at one");

        // Less than it looks, since the primary key on CorrelationId already forbids a second row.
        (await SagaRowsAsync(orderId)).ShouldBeLessThanOrEqualTo(
            1,
            "one instance or none — never two for one CorrelationId");
    }

    [Fact]
    public async Task The_sagas_sends_are_committed_with_its_instance_rather_than_buffered()
    {
        // ADR-032 in its effect: an InboxState row in the saga's transaction, stamped with LastSequenceNumber once
        // the sends staged beside it are delivered, which an in-memory outbox never writes.
        var orderId = Guid.CreateVersion7();
        var placedId = Guid.CreateVersion7();

        await PublishPlacedAsync(orderId, placedId);

        await Eventually(
            () => SagaRowsAsync(orderId),
            expected: 1,
            because: "the arrange half — nothing below can be true before the transition has run");

        // Anti-vacuity: without this, a WHERE matching nothing would read like a delivery not yet made.
        await Eventually(
            () => TransactionalInboxRowsAsync(placedId, deliveredOnly: false),
            expected: 1,
            because: "UseEntityFrameworkOutbox writes this row inside the saga's transaction; with the " +
                "in-memory outbox on the endpoint instead, ordering.InboxState is never written at all");

        await Eventually(
            () => TransactionalInboxRowsAsync(placedId, deliveredOnly: true),
            expected: 1,
            because: "LastSequenceNumber is stamped once the messages staged in ordering.OutboxMessage " +
                "have been delivered — which is the proof they were staged there and not in a " +
                "process-local buffer that a crash would have discarded (#128, ADR-032)");
    }

    [Fact]
    public async Task The_scheduled_timeout_keeps_its_delay_through_the_outbox()
    {
        // The half LastSequenceNumber cannot see: a zeroed delay would expire the stock timeout at once. It shows
        // the timeout did not fire within the wait, not that it fires at §9.6's delay.
        var orderId = Guid.CreateVersion7();

        await PublishPlacedAsync(orderId, Guid.CreateVersion7());

        await Eventually(
            () => SagaRowsAsync(orderId),
            expected: 1,
            because: "the arrange half — the schedule is armed by the transition into AwaitingStock");

        await Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Anti-vacuity before the negative, since a finalised instance has no row to read a state from.
        (await SagaRowsAsync(orderId)).ShouldBe(
            1,
            "the instance must still exist for the state below to mean anything");

        (await fixture.ScalarAsync<string>(
            "SELECT Value = CurrentState FROM ordering.OrderFulfilmentStates WHERE CorrelationId = {0}",
            orderId))
            .ShouldBe(
                "AwaitingStock",
                "§9.6 arms StockTimeout at five minutes on this transition. Still AwaitingStock after " +
                    "five seconds is what says the delay crossed the outbox — a stripped or zeroed one " +
                    "delivers StockReservationExpired at once, and this state handles it");
    }

    /// <summary>Rows in <c>ordering.InboxState</c> for one message, ADR-032's table rather than §9.5's.</summary>
    /// <param name="deliveredOnly">Narrows to rows whose staged messages have been sent.</param>
    private Task<int> TransactionalInboxRowsAsync(Guid messageId, bool deliveredOnly) =>
        fixture.ScalarAsync<int>(
            deliveredOnly
                ? "SELECT Value = COUNT(*) FROM ordering.InboxState " +
                    "WHERE MessageId = {0} AND LastSequenceNumber IS NOT NULL"
                : "SELECT Value = COUNT(*) FROM ordering.InboxState WHERE MessageId = {0}",
            messageId);

    /// <summary>One message's inbox rows on one endpoint, since §9.5's inbox key is the pair.</summary>
    private async Task<IReadOnlyList<InboxMessage>> InboxRowsAsync(Guid messageId, string endpoint) =>
        [.. (await fixture.InboxAsync()).Where(r => r.MessageId == messageId && r.Endpoint == endpoint)];

    private Task<IReadOnlyList<InboxMessage>> SagaInboxRowsAsync(Guid messageId) =>
        InboxRowsAsync(messageId, DependencyInjection.FulfilmentSagaQueue);

    private Task<int> SagaRowsAsync(Guid orderId) =>
        fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM ordering.OrderFulfilmentStates WHERE CorrelationId = {0}",
            orderId);

    /// <summary>A scheduled expiry, published with an id the teardown can wait for.</summary>
    private async Task PublishExpiredAsync(Guid orderId, Guid messageId)
    {
        // The saga alone: nothing else binds a timeout.
        _published.Add((messageId, DependencyInjection.FulfilmentSagaQueue));

        await fixture.Factory.Services
            .GetRequiredService<IBus>()
            .Publish(
                new StockReservationExpired(orderId),
                c => c.MessageId = messageId,
                TestContext.Current.CancellationToken);
    }

    private async Task PublishPlacedAsync(Guid orderId, Guid messageId)
    {
        OrderPlaced placed = new()
        {
            MessageId = messageId,
            CorrelationId = orderId,
            OccurredAt = DateTimeOffset.UtcNow,
            OrderId = orderId,
            CustomerId = Customer,
            TotalAmount = 19.99m,
            Currency = "EUR",
            Lines = [new PlacedLine(Guid.CreateVersion7(), 1, 19.99m)]
        };

        // Added once even when the same id is published twice, since the filter's replay writes no second row.
        if (!_published.Contains((messageId, DependencyInjection.FulfilmentSagaQueue)))
            _published.Add((messageId, DependencyInjection.FulfilmentSagaQueue));

        await fixture.Factory.Services
            .GetRequiredService<IBus>()
            .Publish(
                placed,
                c =>
                {
                    c.MessageId = messageId;
                    c.CorrelationId = placed.CorrelationId;
                },
                TestContext.Current.CancellationToken);
    }

    private async Task PublishCancelledAsync(Guid orderId, Guid messageId, string origin)
    {
        OrderCancelled cancelled = new()
        {
            MessageId = messageId,
            CorrelationId = orderId,
            OccurredAt = DateTimeOffset.UtcNow,
            OrderId = orderId,
            CustomerId = Customer,
            Reason = CancelReasons.CustomerRequest,
            Origin = origin
        };

        _published.Add((messageId, DependencyInjection.FulfilmentSagaQueue));

        await fixture.Factory.Services
            .GetRequiredService<IBus>()
            .Publish(
                cancelled,
                c =>
                {
                    c.MessageId = messageId;
                    c.CorrelationId = cancelled.CorrelationId;
                },
                TestContext.Current.CancellationToken);
    }

    private async Task PublishReleasedAsync(Guid orderId, Guid messageId)
    {
        StockReleased released = new()
        {
            MessageId = messageId,
            CorrelationId = orderId,
            OccurredAt = DateTimeOffset.UtcNow,
            OrderId = orderId
        };

        // The saga alone consumes StockReleased in this service.
        _published.Add((messageId, DependencyInjection.FulfilmentSagaQueue));

        await fixture.Factory.Services
            .GetRequiredService<IBus>()
            .Publish(
                released,
                c =>
                {
                    c.MessageId = messageId;
                    c.CorrelationId = released.CorrelationId;
                },
                TestContext.Current.CancellationToken);
    }

    private async Task PublishReservedAsync(Guid orderId, Guid messageId)
    {
        StockReserved reserved = new()
        {
            MessageId = messageId,
            CorrelationId = orderId,
            OccurredAt = DateTimeOffset.UtcNow,
            OrderId = orderId
        };

        // Both: the saga correlates on StockReserved and StockReservedHandler records it on the aggregate.
        _published.Add((messageId, DependencyInjection.FulfilmentSagaQueue));
        _published.Add((messageId, DependencyInjection.StockEventsQueue));

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

    private async Task PublishReservationFailedAsync(Guid orderId, Guid messageId)
    {
        StockReservationFailed failed = new()
        {
            MessageId = messageId,
            CorrelationId = orderId,
            OccurredAt = DateTimeOffset.UtcNow,
            OrderId = orderId,
            UnavailableProductIds = [Guid.CreateVersion7()]
        };

        // The saga alone consumes a failed reservation in this service.
        _published.Add((messageId, DependencyInjection.FulfilmentSagaQueue));

        await fixture.Factory.Services
            .GetRequiredService<IBus>()
            .Publish(
                failed,
                c =>
                {
                    c.MessageId = messageId;
                    c.CorrelationId = failed.CorrelationId;
                },
                TestContext.Current.CancellationToken);
    }

    private static Task Eventually(Func<Task<int>> read, int expected, string because) =>
        Eventually<int>(read, expected, because);

    private static async Task Eventually<T>(Func<Task<T>> read, T expected, string because)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + DeliveryBudget;
        T actual = default!;

        while (DateTimeOffset.UtcNow < deadline)
        {
            actual = await read();

            if (EqualityComparer<T>.Default.Equals(actual, expected))
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        }

        actual.ShouldBe(expected, because);
    }
}
