using System.Diagnostics;
using Common.Contracts;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Payments.V1;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Payments.Infrastructure.Messaging;
using Payments.TestSupport;
using Shouldly;
using Xunit;
using MessagingRegistration = Payments.Infrastructure.Messaging.DependencyInjection;

namespace Payments.Api.Tests;

/// <summary>
/// §3.2's Accepts column for Payments over a real broker: an
/// <c>AuthorisePayment</c> sent to <c>payments-commands</c>, consumed by the
/// real endpoint with its retry and delayed redelivery, answered by the
/// simulator's own mappings and staged through the outbox. The real transport
/// rather than the harness, because the redelivery is the delayed exchange's
/// (ADR-021) and <c>AddMassTransitTestHarness</c> replaces it.
/// </summary>
[Collection(nameof(IntegrationCollection))]
public sealed class PaymentsCommandEndpointTests(ServiceFixture fixture) : IAsyncLifetime
{
    /// <summary>
    /// How long a sent message is given to settle, on the event suite's
    /// argument: generous for a loaded runner, bounded because an endpoint
    /// that binds nothing never answers late, it never answers.
    /// </summary>
    private static readonly TimeSpan DeliveryBudget = TimeSpan.FromSeconds(30);

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_placed_order_is_authorised_and_PaymentAuthorised_is_staged()
    {
        Guid order = Guid.CreateVersion7();
        await PublishAsync(Placed(order, 42.10m));

        await SendAsync(new AuthorisePayment(order, 42.10m, "EUR"));

        (await StatusAsync(order)).ShouldBe("Authorised");
        (await StagedAsync("PaymentAuthorised")).ShouldBe(1);
        ProviderCalls().ShouldBe(1);
    }

    [Fact]
    public async Task A_scripted_decline_stages_PaymentDeclined()
    {
        Guid order = Guid.CreateVersion7();
        await PublishAsync(Placed(order, 10.01m));

        await SendAsync(new AuthorisePayment(order, 10.01m, "EUR"));

        (await StatusAsync(order)).ShouldBe("Declined");
        (await StagedAsync("PaymentDeclined")).ShouldBe(1);
    }

    [Fact]
    public async Task An_authorisation_before_its_order_waits_and_succeeds_when_the_order_lands()
    {
        Guid order = Guid.CreateVersion7();

        await SendAsync(new AuthorisePayment(order, 42.10m, "EUR"), drain: false);
        await fixture.Orders.Locked(order).WaitAsync(DeliveryBudget, TestContext.Current.CancellationToken);
        (await StatusAsync(order)).ShouldBeNull("§3.2: a missing record is a wait, not a decline");
        (await StagedAsync("PaymentDeclined")).ShouldBe(0);

        await PublishAsync(Placed(order, 42.10m));

        await Eventually(
            async () => await StatusAsync(order) == "Authorised" ? 1 : 0,
            expected: 1,
            because: "the first redelivery, RedeliveryLadder.Intervals[0] later, finds the record",
            budget: RedeliveryLadder.Intervals[0] + TimeSpan.FromSeconds(30));

        // The delayed exchange's wait, not an immediate retry's: a retry
        // would find the record within seconds and pass everything above.
        IReadOnlyList<long> locks = fixture.Orders.LockedAt(order);
        Stopwatch.GetElapsedTime(locks[0], locks[^1]).ShouldBeGreaterThanOrEqualTo(
            RedeliveryLadder.Intervals[0],
            "the attempt that found the record is the redelivery's (§3.2)");
    }

    [Fact]
    public async Task A_cancellation_before_the_authorisation_answers_order_cancelled_and_calls_nobody()
    {
        Guid order = Guid.CreateVersion7();
        await PublishAsync(Placed(order, 42.10m));
        await PublishAsync(Cancelled(order));

        await SendAsync(new AuthorisePayment(order, 42.10m, "EUR"));

        (await StatusAsync(order)).ShouldBe("Declined");
        (await fixture.ScalarAsync<string>(
            "SELECT Value = DeclineReason FROM payments.PaymentIntents WHERE OrderId = {0}",
            order))
            .ShouldBe("order_cancelled");
        ProviderCalls().ShouldBe(0, "ADR-049");
    }

    [Fact]
    public async Task A_mismatch_charges_nothing_and_is_not_retried()
    {
        Guid order = Guid.CreateVersion7();
        await PublishAsync(Placed(order, 42.10m));

        await SendAsync(new AuthorisePayment(order, 99.99m, "EUR"), drain: false);

        await Eventually(
            () => fixture.QueueDepthAsync($"{MessagingRegistration.CommandsQueue}_error"),
            expected: 1,
            because: "a mismatch is excluded from retry and faults straight to the error queue §13.6 pages on");
        (await StatusAsync(order)).ShouldBeNull();
        ProviderCalls().ShouldBe(0);
        (await fixture.InboxAsync()).ShouldNotContain(m => m.Endpoint == MessagingRegistration.CommandsQueue,
            "a fault is not consumed, so no inbox row is written");
    }

    [Fact]
    public async Task A_resend_under_a_fresh_id_is_acknowledged_without_a_second_charge_or_verdict()
    {
        Guid order = Guid.CreateVersion7();
        await PublishAsync(Placed(order, 42.10m));
        await SendAsync(new AuthorisePayment(order, 42.10m, "EUR"));

        await SendAsync(new AuthorisePayment(order, 42.10m, "EUR"));

        (await StagedAsync("PaymentAuthorised")).ShouldBe(1, "the first verdict is the only one");
        ProviderCalls().ShouldBe(1, "one charge");
    }

    [Fact]
    public async Task A_commit_that_fails_after_the_verdict_is_staged_rolls_back_and_the_retry_charges_once()
    {
        Guid order = Guid.CreateVersion7();
        await PublishAsync(Placed(order, 42.10m));
        using CommitFault fault = fixture.FailNextCommit();

        await SendAsync(new AuthorisePayment(order, 42.10m, "EUR"));

        fault.Fired.ShouldBeTrue("the first unit reached its commit with the intent and the outbox row staged");
        (await StatusAsync(order)).ShouldBe("Authorised");
        (await StagedAsync("PaymentAuthorised")).ShouldBe(1, "the rolled-back unit's outbox row went with it");
        fixture.Provider.LogEntries.Count(e => e.RequestMessage!.Path == "/v1/authorisations").ShouldBe(2,
            "the retry replayed the provider call under the same key");
        fixture.Provider.LogEntries.Select(e => e.RequestMessage!.Headers!["Idempotency-Key"].Single()).Distinct()
            .ShouldHaveSingleItem();
    }

    /// <summary>
    /// Sends under a transport id of the caller's or a fresh one and, when
    /// draining, waits for the inbox row under that id: the inbox keys on the
    /// transport id (§9.5), and the row is written only once the unit has
    /// committed, so its arrival is the settled state.
    /// </summary>
    private async Task SendAsync(AuthorisePayment message, bool drain = true, Guid? messageId = null)
    {
        Guid id = messageId ?? Guid.CreateVersion7();

        // IBus rather than a resolved ISendEndpointProvider: the bus is one,
        // and the registered provider is scoped, which the root refuses.
        ISendEndpointProvider sender = fixture.Factory.Services.GetRequiredService<IBus>();
        ISendEndpoint endpoint = await sender.GetSendEndpoint(new Uri($"queue:{MessagingRegistration.CommandsQueue}"));

        await endpoint.Send(message, c => c.MessageId = id, TestContext.Current.CancellationToken);

        if (drain)
        {
            await Eventually(
                async () => (await fixture.InboxAsync(id)).Count,
                expected: 1,
                because: "the inbox row is written after the command has committed (§9.5)");
        }
    }

    private async Task PublishAsync<T>(T message, bool drain = true)
        where T : class, IIntegrationEvent
    {
        await fixture.Factory.Services.GetRequiredService<IBus>().Publish(
            message,
            c =>
            {
                c.MessageId = message.MessageId;
                c.CorrelationId = message.CorrelationId;
            },
            TestContext.Current.CancellationToken);

        if (drain)
        {
            await Eventually(
                async () => (await fixture.InboxAsync(message.MessageId)).Count,
                expected: 1,
                because: "the inbox row is written after the handler's command has committed (§9.5)");
        }
    }

    private static OrderPlaced Placed(Guid order, decimal total = 42.10m) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = order,
        OccurredAt = DateTimeOffset.UtcNow,
        OrderId = order,
        CustomerId = Guid.CreateVersion7(),
        TotalAmount = total,
        Currency = "EUR",
        Lines = [new PlacedLine(Guid.CreateVersion7(), 1, total)]
    };

    private static OrderCancelled Cancelled(Guid order) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = order,
        OccurredAt = DateTimeOffset.UtcNow,
        OrderId = order,
        CustomerId = Guid.CreateVersion7(),
        Reason = CancelReasons.CustomerRequest
    };

    // A subquery, so an order with no intent reads as one null row rather
    // than as no rows, which ScalarAsync's SingleAsync refuses.
    private Task<string?> StatusAsync(Guid order) =>
        fixture.ScalarAsync<string?>(
            "SELECT Value = (SELECT Status FROM payments.PaymentIntents WHERE OrderId = {0})",
            order);

    private async Task<int> StagedAsync(string type) =>
        (await fixture.OutboxAsync()).Count(r => r.MessageType.Contains(type, StringComparison.Ordinal));

    private int ProviderCalls() =>
        fixture.Provider.LogEntries.Count(e => e.RequestMessage!.Path == "/v1/authorisations");

    /// <summary>
    /// Polls rather than sleeps, and fails with the last value it saw. The
    /// budget is a parameter because one outcome here is a redelivery, which
    /// arrives on the ladder's clock rather than the broker's round trip.
    /// </summary>
    private static async Task Eventually(Func<Task<int>> read, int expected, string because, TimeSpan? budget = null)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + (budget ?? DeliveryBudget);
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
