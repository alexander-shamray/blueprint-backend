using Common.Contracts;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Payments.V1;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Payments.TestSupport;
using Shouldly;
using WireMock.RequestBuilders;
using Xunit;
using MessagingRegistration = Payments.Infrastructure.Messaging.DependencyInjection;

// MassTransit declares a Response of its own, so the builder is named rather
// than imported: one `using` would make every Response in this file ambiguous.
using Response = WireMock.ResponseBuilders.Response;

namespace Payments.Api.Tests;

/// <summary>
/// The <c>OrderCancelled</c> table of the spec's section 6 over a real broker
/// and a real engine: an authorised intent is voided and refunded, a declined
/// one publishes nothing (ADR-047).
/// </summary>
/// <remarks>
/// The race is why this is over containers rather than fakes: the lock that
/// closes it is the order record's, taken by a raw statement in SQL Server,
/// and no in-memory double has one.
/// </remarks>
[Collection(nameof(IntegrationCollection))]
public sealed class VoidOnCancellationTests(ServiceFixture fixture) : IAsyncLifetime
{
    /// <summary>
    /// How long a sent message is given to settle, on the command suite's
    /// argument: generous for a loaded runner, bounded because an endpoint
    /// that binds nothing never answers late, it never answers.
    /// </summary>
    private static readonly TimeSpan DeliveryBudget = TimeSpan.FromSeconds(30);

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_cancellation_after_an_authorisation_voids_it_once_and_publishes_PaymentRefunded()
    {
        Guid order = Guid.CreateVersion7();
        await PublishAsync(Placed(order, 42.10m));
        await SendAsync(new AuthorisePayment(order, 42.10m, "EUR"));

        await PublishAsync(Cancelled(order));

        (await RefundCount(order)).ShouldBe(1);
        VoidCalls().ShouldBe(1);
        (await StagedAsync("PaymentRefunded")).ShouldBe(1);
    }

    [Fact]
    public async Task A_second_cancellation_under_a_fresh_id_refunds_nothing_more()
    {
        Guid order = Guid.CreateVersion7();
        await PublishAsync(Placed(order, 42.10m));
        await SendAsync(new AuthorisePayment(order, 42.10m, "EUR"));
        await PublishAsync(Cancelled(order));

        await PublishAsync(Cancelled(order));

        (await RefundCount(order)).ShouldBe(1);
        VoidCalls().ShouldBe(1);
        (await StagedAsync("PaymentRefunded")).ShouldBe(1, "the refund was published; a repeat is not a second refund");
    }

    [Fact]
    public async Task A_cancellation_of_a_declined_payment_publishes_nothing()
    {
        Guid order = Guid.CreateVersion7();
        await PublishAsync(Placed(order, 10.01m));
        await SendAsync(new AuthorisePayment(order, 10.01m, "EUR"));

        await PublishAsync(Cancelled(order));

        (await RefundCount(order)).ShouldBe(0);
        VoidCalls().ShouldBe(0);
        (await StagedAsync("PaymentRefunded")).ShouldBe(0, "ADR-047: PaymentRefunded means money moved back");
    }

    [Fact]
    public async Task A_cancellation_arriving_mid_authorisation_waits_for_it_and_then_voids_it()
    {
        Guid order = Guid.CreateVersion7();
        await PublishAsync(Placed(order, 42.10m));
        Guid command = Guid.CreateVersion7();
        OrderCancelled cancelled = Cancelled(order);
        using ProviderGate gate = fixture.PauseNextAuthorisation();

        await SendAsync(new AuthorisePayment(order, 42.10m, "EUR"), drain: false, messageId: command);
        await gate.Reached.WaitAsync(DeliveryBudget, TestContext.Current.CancellationToken);
        await PublishAsync(cancelled, drain: false);
        await fixture.Orders.Stamping(order).WaitAsync(DeliveryBudget, TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        (await fixture.InboxAsync(cancelled.MessageId)).ShouldBeEmpty(
            "the cancellation entered its stamp and is waiting on the record lock the paused authorisation holds");

        gate.Release();
        await Eventually(
            async () => (await fixture.InboxAsync(command)).Count + (await fixture.InboxAsync(cancelled.MessageId)).Count,
            expected: 2,
            because: "both deliveries are consumed, the cancellation after the authorisation commits");

        (await StatusAsync(order)).ShouldBe("Authorised");
        (await RefundCount(order)).ShouldBe(1, "the cancellation saw the committed authorisation and voided it");
        AuthoriseCalls().ShouldBe(1);
        VoidCalls().ShouldBe(1);
    }

    [Fact]
    public async Task A_void_whose_commit_fails_is_replayed_under_the_same_key_and_refunds_once()
    {
        Guid order = Guid.CreateVersion7();
        await PublishAsync(Placed(order, 42.10m));
        await SendAsync(new AuthorisePayment(order, 42.10m, "EUR"));
        using CommitFault fault = fixture.FailNextCommit();

        await PublishAsync(Cancelled(order));

        fault.Fired.ShouldBeTrue("the first unit voided and staged the refund before its commit failed");
        (await RefundCount(order)).ShouldBe(1);
        (await StagedAsync("PaymentRefunded")).ShouldBe(1, "the rolled-back unit's refund event went with it");
        VoidCalls().ShouldBe(2, "the retry replayed the void rather than skipping it");
        fixture.Provider.LogEntries
            .Where(e => e.RequestMessage!.Path.EndsWith("/void", StringComparison.Ordinal))
            .Select(e => e.RequestMessage!.Headers!["Idempotency-Key"].Single())
            .Distinct().ShouldHaveSingleItem().ShouldBe($"void:{order}");
    }

    [Fact]
    public async Task A_void_the_provider_refuses_as_a_mismatch_is_not_retried()
    {
        Guid order = Guid.CreateVersion7();
        await PublishAsync(Placed(order, 42.10m));
        await SendAsync(new AuthorisePayment(order, 42.10m, "EUR"));
        fixture.Provider.Given(Request.Create().WithPath("/v1/authorisations/*/void").UsingPost())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(409));

        await PublishAsync(Cancelled(order), drain: false);

        await Eventually(
            () => fixture.QueueDepthAsync($"{MessagingRegistration.EventsQueue}_error"),
            expected: 1,
            because: "a 409 on the void key is excluded from retry and faults straight to the error queue");
        VoidCalls().ShouldBe(1, "one consumer attempt, and the provider was asked once");
        (await RefundCount(order)).ShouldBe(0);
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

    private Task<int> RefundCount(Guid order) =>
        fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM payments.Refunds WHERE OrderId = {0}", order);

    private int VoidCalls() =>
        fixture.Provider.LogEntries.Count(e =>
            e.RequestMessage!.Path.EndsWith("/void", StringComparison.Ordinal));

    private int AuthoriseCalls() =>
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
