using Common.Contracts;
using Common.Contracts.Inventory.V1;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Payments.V1;
using Common.Contracts.Shipping.V1;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Infrastructure.Messaging;
using Shouldly;
using Xunit;

namespace Ordering.Application.Tests;

/// <summary>
/// §12.5's suite: §9.6's saga driven end to end over MassTransit's in-memory
/// harness, with no infrastructure at all. It lives here rather than in
/// <c>Ordering.Api.Tests</c> because the suites that need Docker pay for a
/// container set each (§12.4), and this one needs none.
/// </summary>
/// <remarks>
/// The saga is <c>Ordering.Infrastructure</c>'s, which this project already
/// references for <c>AddOrderingInfrastructure</c>; §4.2's gate binds
/// <c>Ordering.Application</c>, not its tests.
/// </remarks>
public class OrderFulfilmentSagaTests
{
    /// <summary>
    /// §12.5's two bounds, stated rather than inherited, and stated once per
    /// harness rather than per test.
    /// </summary>
    /// <remarks>
    /// An <c>Any(…)</c> ends at the earliest of a match, the inactivity bound
    /// (1.2 s by default, from the last bus activity), the test bound (from the
    /// call) and the caller's token. Inherit either and a saturated runner
    /// fails the suite wearing the assertion's own message. The ceiling is kept
    /// clear of the bound meant to fire, so which one reported a failure is
    /// never a detail of how long a publish took.
    /// </remarks>
    private static readonly TimeSpan InactivityTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(60);

    private static readonly Guid Customer = Guid.Parse("2a1c9e64-77b1-4b0e-9a3e-6d9c1c2f5a11");

    /// <summary>
    /// The registration every test shares, with the scheduler §12.5's sample
    /// omits.
    /// </summary>
    /// <remarks>
    /// <c>Initially</c> arms <c>StockTimeout</c>, so the first
    /// <c>OrderPlaced</c> reaches for a scheduler nothing else puts on the
    /// pipeline; without the two scheduler lines the saga faults to the error
    /// queue and every waiting assertion fails as a timeout rather than an
    /// error. They are the lines production uses (ADR-021): the in-memory
    /// transport implements the delay itself, so the transports differ and the
    /// registration under test does not.
    /// </remarks>
    private static ServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddMassTransitTestHarness(x =>
            {
                x.SetTestTimeouts(TestTimeout, InactivityTimeout);
                x.AddDelayedMessageScheduler();
                x
                    .AddSagaStateMachine<OrderFulfilmentSaga, OrderFulfilmentState>()
                    .InMemoryRepository();
                x.UsingInMemory((context, cfg) =>
                {
                    cfg.UseDelayedMessageScheduler();
                    cfg.ConfigureEndpoints(context);
                });
            })
            .BuildServiceProvider(true);

    private static async Task<(ServiceProvider Provider, ITestHarness Harness)> StartHarnessAsync()
    {
        ServiceProvider provider = BuildProvider();
        ITestHarness harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        return (provider, harness);
    }

    /// <summary>
    /// Publishes, and does not return until that message has been consumed — by
    /// the saga, or by whatever consumer is bound to it. A fault counts as
    /// consumed, so this is a claim about ordering and never about outcome.
    /// </summary>
    /// <remarks>
    /// A publish returns when the message reaches the transport, not when the
    /// saga has consumed it, so two unfenced publishes are a race the runner
    /// loses under load, failing a later assertion wearing the wrong
    /// component's name. The barrier is here rather than at the call sites
    /// because per-site discipline fails open. It waits on this message's id,
    /// not its type, so a second delivery of one type is fenced too; the id is
    /// read from the send context because a scheduled expiry has no envelope,
    /// and for a contract the two are one value (§9.1). Only a type no consumer
    /// takes spends the inactivity bound, and that is a real defect reported
    /// where it happened.
    /// </remarks>
    private static async Task Publish<T>(ITestHarness harness, T message)
        where T : class
    {
        Guid? messageId = null;
        await harness.Bus.Publish(
            message,
            context =>
            {
                // §9.1: body, row, header and inbox key are one GUID, and
                // IIntegrationEvent says CorrelationId follows the same rule.
                // Letting MassTransit mint its own would give every event two
                // identities, and nothing here would fail for it.
                if (message is IIntegrationEvent integrationEvent)
                {
                    context.MessageId = integrationEvent.MessageId;
                    context.CorrelationId = integrationEvent.CorrelationId;
                }

                // A scheduled expiry is not a contract and has no envelope:
                // §3.2 lists it in no column and §4.3 keeps it private to the
                // saga. The send context is the one handle both kinds carry,
                // and the wait reads it.
                messageId = context.MessageId;
            },
            TestContext.Current.CancellationToken);

        // Unset, this degrades into a type-level wait — null == null matches
        // the first consume of T and fences nothing — so it fails loudly
        // instead.
        messageId.ShouldNotBeNull();

        (await ConsumedWithId<T>(harness, messageId)).ShouldBeTrue(
            $"a published {typeof(T).Name} was never consumed, so this barrier cannot say the " +
            "next publish is ordered after it — an unfenced publish is a race the runner loses " +
            "under load, and it fails a later assertion wearing the wrong component's name.");
    }

    private static Task<bool> Sent<T>(ITestHarness harness, Func<T, bool> match)
        where T : class =>
        harness.Sent.Any<T>(m => match(m.Context.Message), TestContext.Current.CancellationToken);

    private static Task<bool> Consumed<T>(ITestHarness harness, Func<T, bool> match)
        where T : class =>
        harness.Consumed.Any<T>(m => match(m.Context.Message), TestContext.Current.CancellationToken);

    /// <summary>
    /// <see cref="Consumed"/> over the send context's message id, which is what
    /// <see cref="Publish"/> needs and no test does.
    /// </summary>
    private static Task<bool> ConsumedWithId<T>(ITestHarness harness, Guid? messageId)
        where T : class =>
        harness.Consumed.Any<T>(m => m.Context.MessageId == messageId, TestContext.Current.CancellationToken);

    /// <summary>
    /// A negative assertion read as of now: no wait, no deadline for a late
    /// saga to hide inside, and the harness's one shared inactivity token left
    /// unspent for whatever follows (§12.5).
    /// </summary>
    /// <remarks>
    /// A deadline would fail open: a window is something a late-sending saga
    /// fits inside, so "not yet" needs a point in time to be false at. Where
    /// the negative follows a publish, <see cref="Publish"/> is that point,
    /// since it returns only once the saga has consumed the message; a negative
    /// asserted anywhere else still needs the caller to pin the moment. §12.5
    /// permits a trailing negative to simply wait, but waiting costs the full
    /// inactivity bound for an answer already knowable, so every negative here
    /// goes through this or <see cref="NotYetPublished"/>.
    /// </remarks>
    private static Task<bool> NotYetSent<T>(ITestHarness harness, Func<T, bool> match)
        where T : class =>
        harness.Sent.Any<T>(m => match(m.Context.Message), Spent());

    /// <summary>
    /// <see cref="NotYetSent"/>'s sibling over the published list, for the one
    /// negative here that asserts a command was not published.
    /// </summary>
    private static Task<bool> NotYetPublished<T>(ITestHarness harness, Func<T, bool> match)
        where T : class =>
        harness.Published.Any<T>(m => match(m.Context.Message), Spent());

    /// <summary>
    /// An already-cancelled token, so an assertion reads the record as of now
    /// rather than waiting for something the test has just proved will not
    /// come.
    /// </summary>
    /// <remarks>
    /// The constructor, not a cancelled <c>CancellationTokenSource</c>: §12.5's
    /// source form is written inside the test, where the <c>using</c> scope
    /// outlives the assertion. Behind a helper the source is disposed on
    /// return, and a token whose source is disposed still reports
    /// <c>IsCancellationRequested</c> while throwing
    /// <c>ObjectDisposedException</c> from <c>Register</c>. Cancelled on
    /// construction, it owns nothing that can be disposed from under a caller.
    /// </remarks>
    private static CancellationToken Spent() => new(canceled: true);

    /// <summary>
    /// The exception each recorded consume of <typeparamref name="T"/> ended
    /// with, or null where it ended cleanly.
    /// </summary>
    /// <remarks>
    /// The harness records a consume whether the pipeline threw or not, so
    /// <see cref="Consumed"/> answers "did it arrive" and never "what happened
    /// to it"; a saga event that no longer applies faults by default (§9.6),
    /// and every negative in this file stays green through it. Read the list
    /// only once the delivery it asks about has been pinned, which for a
    /// published message <see cref="Publish"/> did. <see cref="Spent"/> is
    /// load-bearing: the token-less overload enumerates until the harness's one
    /// shared inactivity bound, and a mid-test read that spends it makes every
    /// assertion after it answer immediately and falsely.
    /// </remarks>
    private static IEnumerable<Exception?> ConsumeFaults<T>(ITestHarness harness)
        where T : class =>
        harness.Consumed.Select<T>(Spent()).Select(m => m.Exception);

    [Fact]
    public async Task Commands_are_sent_and_events_are_published()
    {
        // §9.6's distinction: a command published rather than sent would reach
        // every subscriber that bound the type, and nothing else here would
        // notice.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));

            // The positive first gives the negative a point in time to be false
            // at.
            (await Sent<ReserveStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();
            (await NotYetPublished<ReserveStock>(harness, m => m.OrderId == orderId)).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task The_order_lines_are_mapped_rather_than_forwarded()
    {
        // ReserveStock owns its line type, so versioning OrderPlaced does not
        // version Inventory's command (§9.6); a StockLine has no price, which
        // is the point of the separate type.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));

            (await Sent<ReserveStock>(harness, m =>
                m.OrderId == orderId &&
                m.Lines.Count == 1 &&
                m.Lines[0].ProductId == SagaContracts.Product &&
                m.Lines[0].Quantity == 2))
                .ShouldBeTrue();
        }
    }

    [Fact]
    public async Task Payment_declined_releases_stock_before_cancelling()
    {
        // Appendix C names this one: the payment-declined compensation
        // ordering.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            // The Sent waits assert the command each transition owes; the
            // ordering is Publish's job, so a test with no such assertion needs
            // no wait either.
            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            (await Sent<ReserveStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.StockReserved(orderId));
            (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.PaymentDeclined(orderId, "insufficient_funds"));

            (await Sent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            // CancelOrder must not go until the release is confirmed. The
            // Publish above is the point in time this is false at: it returned
            // only once the saga had consumed PaymentDeclined.
            (await NotYetSent<CancelOrder>(harness, m => m.OrderId == orderId)).ShouldBeFalse();

            await Publish(harness, SagaContracts.StockReleased(orderId));

            // The reason, not just the send: both exits from Compensating read
            // ctx.Saga.CancelReason, so a transition that forgets to set it on
            // entry sends a CancelOrder carrying null, which an unqualified
            // assertion passes.
            (await Sent<CancelOrder>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == CancelReasons.PaymentDeclined))
                .ShouldBeTrue();
        }
    }

    [Fact]
    public async Task A_payment_timeout_compensates_with_its_own_reason()
    {
        // The same compensation as a decline and deliberately not the same
        // reason: the two are one dimension value apart on orders.cancelled
        // (§13.3) and a different incident.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));

            // Published directly rather than waited for: the schedule is
            // fifteen minutes, and what is under test is the transition, not
            // the timer.
            await Publish(harness, new PaymentAuthorisationExpired(orderId));

            (await Sent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.StockReleased(orderId));

            (await Sent<CancelOrder>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == CancelReasons.PaymentTimeout))
                .ShouldBeTrue();
        }
    }

    [Fact]
    public async Task A_failed_reservation_cancels_out_of_stock()
    {
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReservationFailed(orderId));

            (await Sent<CancelOrder>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == CancelReasons.OutOfStock))
                .ShouldBeTrue();

            // No compensation: nothing was reserved, so nothing is released.
            (await NotYetSent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task A_stock_timeout_cancels_with_its_own_reason()
    {
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, new StockReservationExpired(orderId));

            (await Sent<CancelOrder>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == CancelReasons.StockTimeout))
                .ShouldBeTrue();
        }
    }

    [Fact]
    public async Task A_timeout_that_arrives_after_its_wait_has_ended_changes_nothing()
    {
        // The stock timeout is unscheduled on StockReserved, but a copy already
        // in flight still arrives and must not cancel a paid order (§9.8).
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));

            (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, new StockReservationExpired(orderId));

            // The point in time the negative is false at: the stale timeout has
            // been delivered and had nothing to match.
            (await Consumed<StockReservationExpired>(harness, m => m.OrderId == orderId))
                .ShouldBeTrue();
            (await NotYetSent<CancelOrder>(harness, m => m.OrderId == orderId)).ShouldBeFalse();

            // "Changes nothing" includes not faulting: a saga event that does
            // not apply throws by default, and the two assertions above are
            // green either way. ADR-021 leans on this being harmless, since its
            // scheduler cancels nothing and every timeout fires.
            ConsumeFaults<StockReservationExpired>(harness).ShouldAllBe(e => e == null);
        }
    }

    [Fact]
    public async Task A_redelivered_event_faults_rather_than_being_absorbed_silently()
    {
        // §9.8's inbox suppresses a completed redelivery, but its row is
        // written after the consumer returns, so a crash between the saga state
        // committing and that write leaves the next delivery free to land on an
        // instance that has moved on. It faults, and the fault is what is
        // asserted: absorbing silently would answer a misroute the same way as
        // a duplicate. This harness registers no inbox or outbox, so the sends
        // committing with the instance (ADR-032) is asserted in the endpoint
        // suite, not here.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));

            (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            // A second delivery carrying its own message id: this harness
            // configures no inbox, so the id decides nothing and the stimulus
            // is the state machine's input either way.
            StockReserved redelivered = SagaContracts.StockReserved(orderId);
            await Publish(harness, redelivered);

            (await Consumed<StockReserved>(harness, m => m.MessageId == redelivered.MessageId))
                .ShouldBeTrue();

            // §12.5 reads Exception rather than an effect because "no
            // transition ran" is what both a silent absorb and a fault look
            // like from every other assertion.
            ConsumeFaults<StockReserved>(harness)
                .ShouldContain(
                    e => e != null,
                    "an event no transition accepts must reach the error queue §13.6 pages on. " +
                    "Absorbing it silently would answer a lost-command crash and a misroute the " +
                    "same way it answers a duplicate (#128).");

            // One authorisation for one order, read as of now: the positive
            // above is the point in time, and a waiting read would give a late
            // second send somewhere to hide.
            harness.Sent
                .Select<AuthorisePayment>(Spent())
                .Count(m => m.Context.Message.OrderId == orderId)
                .ShouldBe(1);
        }
    }

    [Fact]
    public async Task Authorised_payment_confirms_the_order_and_waits_for_despatch()
    {
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));

            // Currency travels with the amount — a bare decimal is a charge
            // waiting to be made in the wrong denomination (§9.6).
            (await Sent<AuthorisePayment>(harness, m =>
                m.OrderId == orderId &&
                m.Amount == SagaContracts.Total &&
                m.Currency == SagaContracts.Currency))
                .ShouldBeTrue();

            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "psp-ref-1"));

            (await Sent<ConfirmOrder>(harness, m =>
                m.OrderId == orderId &&
                m.PaymentReference == "psp-ref-1"))
                .ShouldBeTrue();

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            // Sending ConfirmOrder is not confirming the order: the machine
            // waits for the aggregate's own acknowledgement.
            (await saga.Exists(orderId, x => x.AwaitingConfirmation)).ShouldNotBeNull();

            await Publish(harness, SagaContracts.OrderConfirmed(orderId, Customer));

            // Not finalised: the instance has to survive to time the despatch
            // out, and a wait the machine cannot represent is a wait it cannot
            // time out.
            (await saga.Exists(orderId, x => x.Confirmed)).ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task A_cancellation_before_the_confirmation_lands_releases_the_stock()
    {
        // AwaitingConfirmation means ConfirmOrder is in flight and nothing
        // downstream has been told, so Shipping has no despatch to prepare and
        // the reservation is released rather than stranded.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            (await Sent<ReserveStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.StockReserved(orderId));
            (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "psp-ref-126"));
            (await Sent<ConfirmOrder>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.Exists(orderId, x => x.AwaitingConfirmation)).ShouldNotBeNull();

            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest));

            (await Sent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();
            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();

            // No review row: Payments voids off OrderCancelled itself (§3.2),
            // and what makes the confirmed case a human's problem is a despatch
            // that might already be moving, of which there is none here.
            harness.Sent
                .Select<FlagOrderForReview>(Spent())
                .ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task A_confirmation_arriving_after_compensation_began_escalates()
    {
        // OrderConfirmed and OrderCancelled are both Ordering's outbox rows and
        // §9.4 orders nothing between them, so the cancellation can reach the
        // saga first; the confirmation landing afterwards is the only evidence
        // that Shipping has been told and the ReleaseStock in flight is for
        // stock somebody may be picking.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            (await Sent<ReserveStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.StockReserved(orderId));
            (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "psp-ref-127"));
            (await Sent<ConfirmOrder>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest));

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();

            await Publish(harness, SagaContracts.OrderConfirmed(orderId, Customer));

            (await Sent<FlagOrderForReview>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == ReviewReasons.CancelledAfterConfirmation))
                .ShouldBeTrue();

            ConsumeFaults<OrderConfirmed>(harness).ShouldAllBe(e => e == null);

            // Still waiting on Inventory — the exits own the cancellation, so
            // this branch adds the row and nothing else.
            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task A_confirmation_that_never_arrives_escalates_rather_than_hanging()
    {
        // The aggregate refusing ConfirmOrder is not this case — that is a Rule
        // failure CommandConsumer acks, and the cancellation behind it reaches
        // the saga on its own event. This is the command never being consumed
        // at all, with the card authorised and the stock held.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            (await Sent<ReserveStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.StockReserved(orderId));
            (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "psp-ref-128"));
            (await Sent<ConfirmOrder>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.Exists(orderId, x => x.AwaitingConfirmation)).ShouldNotBeNull();

            // Driven rather than waited out: the schedule is ten minutes, and a
            // test that slept for it is a test nobody runs.
            await Publish(harness, new ConfirmationExpired(orderId));

            (await Sent<FlagOrderForReview>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == ReviewReasons.NotConfirmed))
                .ShouldBeTrue();

            // No CancelOrder: §3.2 gives Ordering no refund command, so there
            // is nothing to compensate with.
            harness.Sent
                .Select<CancelOrder>(Spent())
                .Count(m => m.Context.Message.OrderId == orderId)
                .ShouldBe(0);

            (await saga.NotExists(orderId)).ShouldBeNull();
        }
    }

    [Fact]
    public async Task Despatch_marks_the_order_shipped_and_finalises()
    {
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            (await Sent<ReserveStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.StockReserved(orderId));
            (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "psp-ref-2"));
            (await Sent<ConfirmOrder>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            // The acknowledgement is what puts the saga in Confirmed.
            // AwaitingConfirmation binds ShipmentDispatched too, so this wait
            // is what keeps the test on the ordinary path.
            await Publish(harness, SagaContracts.OrderConfirmed(orderId, Customer));

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> confirmed =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await confirmed.Exists(orderId, x => x.Confirmed)).ShouldNotBeNull();

            await Publish(harness, SagaContracts.ShipmentDispatched(orderId, "TRACK-9"));

            (await Sent<MarkOrderShipped>(harness, m =>
                m.OrderId == orderId &&
                m.TrackingNumber == "TRACK-9"))
                .ShouldBeTrue();

            // SetCompletedWhenFinalized deletes the instance, which is why
            // §9.6's diagram has no Shipped state: it would be one no saga is
            // ever observed in.
            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.NotExists(orderId)).ShouldBeNull();
        }
    }

    [Fact]
    public async Task A_despatch_timeout_escalates_rather_than_compensating()
    {
        // The wait with no automatic compensation: payment is taken and stock
        // is gone, so the timeout escalates to a human instead, and "no
        // timeout" is not the alternative (§9.6).
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            (await Sent<ReserveStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.StockReserved(orderId));
            (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "psp-ref-3"));
            (await Sent<ConfirmOrder>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            // DespatchTimeout is armed on entering Confirmed, and the expiry
            // below is discarded unless the token is set, so waiting for
            // Confirmed is what makes this drive a transition rather than a
            // no-op.
            await Publish(harness, SagaContracts.OrderConfirmed(orderId, Customer));

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> armed =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await armed.Exists(orderId, x => x.Confirmed)).ShouldNotBeNull();

            await Publish(harness, new DespatchExpired(orderId));

            (await Sent<FlagOrderForReview>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == ReviewReasons.NotDespatched))
                .ShouldBeTrue();

            // Not cancelled, and this is what separates an escalation from a
            // compensation: the customer has paid and the parcel may yet leave.
            (await NotYetSent<CancelOrder>(harness, m => m.OrderId == orderId)).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task A_release_timeout_cancels_the_order_and_escalates_the_stock()
    {
        // Two sends from one transition, answering different people: the
        // customer must not wait on Inventory, so the order is cancelled
        // regardless; the stranded reservation is Inventory's to resolve, so it
        // is escalated separately.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));
            await Publish(harness, SagaContracts.PaymentDeclined(orderId, "do_not_honour"));
            await Publish(harness, new StockReleaseExpired(orderId));

            (await Sent<CancelOrder>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == CancelReasons.PaymentDeclined))
                .ShouldBeTrue();

            (await Sent<FlagOrderForReview>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == ReviewReasons.StockNotReleased))
                .ShouldBeTrue();
        }
    }

    [Fact]
    public async Task A_cancellation_while_awaiting_stock_requests_release_and_sends_no_authorisation()
    {
        // §11.4's endpoint cancels the aggregate; until the machine declared
        // Event<OrderCancelled> the saga went on reserving stock and
        // authorising a card for a cancelled order. ReserveStock is in flight
        // here, so the reservation may or may not exist: Compensating releases
        // it and waits, because a release nobody waits on is a reservation
        // nobody notices is stranded. The name says "requests" release because
        // a ReleaseStock sent is all this harness can see; what Inventory does
        // with it is ADR-024's.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            (await Sent<ReserveStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest));

            (await Sent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            // The reservation lands after the cancellation, and nothing may
            // charge.
            StockReserved late = SagaContracts.StockReserved(orderId);
            await Publish(harness, late);

            (await Consumed<StockReserved>(harness, m => m.MessageId == late.MessageId)).ShouldBeTrue();
            (await NotYetSent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeFalse();

            // Absorbed rather than filed: Compensating writes
            // Ignore(StockReserved) explicitly, and without it the event
            // faults.
            ConsumeFaults<StockReserved>(harness).ShouldAllBe(e => e == null);

            await Publish(harness, SagaContracts.StockReleased(orderId));

            (await Sent<CancelOrder>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == CancelReasons.CustomerRequest))
                .ShouldBeTrue();

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.NotExists(orderId)).ShouldBeNull();
        }
    }

    [Fact]
    public async Task A_cancellation_while_awaiting_payment_compensates_and_sends_no_second_authorisation()
    {
        // Stock is held and AuthorisePayment has already gone, so what must not
        // happen is a second authorisation. Whether the first completes is
        // Payments' race, which this transition does not guarantee.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));

            (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest));

            (await Sent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.StockReleased(orderId));

            (await Sent<CancelOrder>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == CancelReasons.CustomerRequest))
                .ShouldBeTrue();

            harness.Sent
                .Select<AuthorisePayment>(Spent())
                .Count(m => m.Context.Message.OrderId == orderId)
                .ShouldBe(1);
        }
    }

    [Fact]
    public async Task A_cancellation_carries_its_own_reason_into_compensation_from_AwaitingStock()
    {
        // A reason no other cancellation test here uses, so a transition that
        // records a literal instead of the event's reason fails rather than
        // matching a copy of the right answer. §11.4 parses the whole
        // CancellationReasons map, so any code can arrive; payment_declined is
        // the one a reader would assume only the saga produces.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            (await Sent<ReserveStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.PaymentDeclined));

            (await Sent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.StockReleased(orderId));

            (await Sent<CancelOrder>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == CancelReasons.PaymentDeclined))
                .ShouldBeTrue();
        }
    }

    [Fact]
    public async Task A_cancellation_carries_its_own_reason_into_compensation_from_AwaitingPayment()
    {
        // A separate test rather than a theory case: the two transitions are
        // separate lines, and a gate that pins one of a copied pair leaves the
        // copy free to drift.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));

            (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.OutOfStock));

            (await Sent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.StockReleased(orderId));

            (await Sent<CancelOrder>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == CancelReasons.OutOfStock))
                .ShouldBeTrue();
        }
    }

    [Fact]
    public async Task A_payment_authorised_while_compensating_escalates_rather_than_being_ignored()
    {
        // Money arriving after a cancellation must not be swallowed. It is
        // Confirmed's case by the other door — the same symptom under a
        // different code — and the difference is shipping: this state cannot
        // despatch and Confirmed may. Not the refund: Payments voids off
        // OrderCancelled (§3.2) on both, and whether it has is not knowable
        // from either state, since §9.4 orders nothing between independent
        // consumers.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));

            (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest));

            (await Sent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "auth-late"));

            (await Sent<FlagOrderForReview>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == ReviewReasons.PaymentAuthorisedDuringCompensation))
                .ShouldBeTrue();

            // And it did not reach the error queue: the point is that this is
            // handled, not merely that it is loud.
            ConsumeFaults<PaymentAuthorised>(harness).ShouldAllBe(e => e == null);

            // The saga is still running: this row is raised mid-wait and can
            // sit beside a live instance until StockReleased or the
            // ReleaseTimeout, which is what the runbook has to say.
            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task A_release_does_not_finalise_while_Payments_still_owes_a_verdict()
    {
        // The ordinary interleaving: Inventory answers promptly and the PSP is
        // slow. An unconditional Finalize on the release deletes the instance,
        // and the authorisation still in flight then correlates to nothing —
        // consumed cleanly, no review row, nothing on §13.6's pager. §9.4
        // orders nothing between the two services, so neither order may be
        // assumed.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));

            (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest));

            (await Sent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            // The order is cancelled on this transition — that command does not
            // wait on Payments — but the instance is held, because the
            // authorisation can still land.
            await Publish(harness, SagaContracts.StockReleased(orderId));

            (await Sent<CancelOrder>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == CancelReasons.CustomerRequest))
                .ShouldBeTrue();

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            // Everything below is unreachable unless the instance survives the
            // release.
            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();

            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "auth-after-release"));

            (await Sent<FlagOrderForReview>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == ReviewReasons.PaymentAuthorisedDuringCompensation))
                .ShouldBeTrue();

            // Both halves settled, so the saga ends: holding the instance is
            // the mechanism, and a saga that never finalised would fire §13.6's
            // unfinalised-saga alert on every cancelled order.
            (await saga.NotExists(orderId)).ShouldBeNull();
        }
    }

    [Fact]
    public async Task A_cancellation_with_no_authorisation_outstanding_still_finalises_on_the_release()
    {
        // Cancelling in AwaitingStock means AuthorisePayment was never sent, so
        // nothing is owed and the release ends the saga: the conditional
        // Finalize is a condition, not a delay.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest));

            (await Sent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.StockReleased(orderId));

            (await Sent<CancelOrder>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.NotExists(orderId)).ShouldBeNull();
        }
    }

    [Fact]
    public async Task A_verdict_that_never_arrives_bounds_the_wait_and_escalates_nothing()
    {
        // Holding the instance for a verdict needs something that ends the hold
        // when none comes, or a slow PSP parks the saga until §13.6's
        // unfinalised-saga alert pages. No FlagOrderForReview, by decision:
        // §3.2 has Payments consuming OrderCancelled, so an authorisation
        // abandoned on a cancelled order is the healthy path, and a row here
        // would escalate it once per cancelled order.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));
            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest));
            await Publish(harness, SagaContracts.StockReleased(orderId));

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();

            // The wait armed when AuthorisePayment was sent, deliberately not
            // unscheduled by the cancellation branch.
            await Publish(harness, new PaymentAuthorisationExpired(orderId));

            (await saga.NotExists(orderId)).ShouldBeNull();

            (await NotYetSent<FlagOrderForReview>(harness, m => m.OrderId == orderId)).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task The_payment_timeout_door_re_arms_its_own_wait_and_the_second_expiry_ends_it()
    {
        // Cancelling in AwaitingPayment leaves the original wait armed, so that
        // door gets its bound for free; reaching Compensating through
        // PaymentTimeout.Received does not, because the wait it would rely on
        // is the one that just fired. Without the re-arm
        // PaymentVerdictOutstanding stays set with nothing left to clear it,
        // and the instance is held until §13.6's unfinalised-saga alert pages.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));

            (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            // A timeout is not a verdict, so the obligation stays outstanding.
            await Publish(harness, new PaymentAuthorisationExpired(orderId));

            (await Sent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.StockReleased(orderId));

            (await Sent<CancelOrder>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == CancelReasons.PaymentTimeout))
                .ShouldBeTrue();

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            // The stock half is settled and the saga still will not end,
            // because this door arrived owing a verdict.
            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();

            // The second expiry, the one the branch armed on its way in.
            await Publish(harness, new PaymentAuthorisationExpired(orderId));

            (await saga.NotExists(orderId)).ShouldBeNull();

            (await NotYetSent<FlagOrderForReview>(harness, m => m.OrderId == orderId)).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task A_decline_after_the_release_settles_the_join_without_escalating()
    {
        // A decline is an answer: no money moved, so it raises nothing, but it
        // discharges the obligation the cancellation carried in and the saga
        // ends on it rather than waiting out the payment window.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));
            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest));
            await Publish(harness, SagaContracts.StockReleased(orderId));

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            // The instance has to be observed alive when the decline arrives: a
            // decline reaching no instance is discarded, and "no instance, no
            // review row" reads identically from both sides.
            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();

            await Publish(harness, SagaContracts.PaymentDeclined(orderId, "do_not_honour"));

            (await saga.NotExists(orderId)).ShouldBeNull();

            (await NotYetSent<FlagOrderForReview>(harness, m => m.OrderId == orderId)).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task A_release_timeout_holds_the_instance_while_a_verdict_is_outstanding()
    {
        // Giving up on the release settles the stock half exactly as
        // StockReleased does, so it asks the same question about the other.
        // "Settled" means come to rest: this branch escalates
        // stock_not_released and the instance stays for the verdict, so one
        // order can carry both rows.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));
            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest));
            await Publish(harness, new StockReleaseExpired(orderId));

            (await Sent<FlagOrderForReview>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == ReviewReasons.StockNotReleased))
                .ShouldBeTrue();

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();

            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "auth-after-timeout"));

            (await Sent<FlagOrderForReview>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == ReviewReasons.PaymentAuthorisedDuringCompensation))
                .ShouldBeTrue();

            (await saga.NotExists(orderId)).ShouldBeNull();
        }
    }

    [Fact]
    public async Task A_cancellation_after_confirmation_escalates_rather_than_compensating()
    {
        // A cancellation this machine cannot compensate itself: undoing an
        // authorisation is a refund §3.2 gives Ordering no command for, and
        // whether Payments has voided off OrderCancelled is not knowable here
        // (§9.4). What the row escalates is shipping: a confirmed order may
        // still despatch. What stops a false not_despatched review three days
        // later is the Finalize, not the Unschedule beside it: ADR-021's
        // scheduler cannot cancel, so the timeout stays queued and is discarded
        // for want of an instance.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));
            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "psp-ref-4"));

            // A send is all a send establishes: the harness registers no
            // command consumer, so the acknowledgement has to be driven for the
            // state below to mean what its name says.
            (await Sent<ConfirmOrder>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.OrderConfirmed(orderId, Customer));

            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest));

            // The Confirmed code, not Compensating's: the row persists nothing
            // else, and the runbook selects its procedure on it after the saga
            // state is gone.
            (await Sent<FlagOrderForReview>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == ReviewReasons.CancelledAfterConfirmation))
                .ShouldBeTrue();

            // Not a compensation: the reservation is being picked, and telling
            // Inventory to drop it is not this machine's call to make.
            (await NotYetSent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeFalse();

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.NotExists(orderId)).ShouldBeNull();
        }
    }

    [Fact]
    public async Task A_cancellation_the_saga_itself_caused_finds_no_instance_and_is_discarded()
    {
        // The routine echo: this order's CancelOrder went out of a branch that
        // finalises, so the OrderCancelled the aggregate then publishes reaches
        // a deleted instance. It must be discarded rather than faulted, or
        // every cancelled order pages someone (§13.6). StockReservationFailed
        // is driven because its branch finalises unconditionally, and
        // CancelOrigins.Workflow is what makes the publish the echo rather than
        // its type alone.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReservationFailed(orderId));

            (await Sent<CancelOrder>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == CancelReasons.OutOfStock))
                .ShouldBeTrue();

            OrderCancelled echo = SagaContracts.OrderCancelled(
                orderId,
                Customer,
                CancelReasons.OutOfStock,
                CancelOrigins.Workflow);
            await Publish(harness, echo);

            (await Consumed<OrderCancelled>(harness, m => m.MessageId == echo.MessageId)).ShouldBeTrue();

            ConsumeFaults<OrderCancelled>(harness).ShouldAllBe(e => e == null);
        }
    }

    [Fact]
    public async Task A_decline_arriving_after_a_cancellation_is_absorbed_rather_than_faulted()
    {
        // Cancelling from AwaitingPayment arrives in Compensating with
        // AuthorisePayment already sent and unanswered, so the PSP's verdict
        // can still be either. A decline means no money moved, which is where
        // compensation was heading, so it is an explicit Ignore rather than an
        // error-queue entry.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));

            (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest));

            (await Sent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            PaymentDeclined declined = SagaContracts.PaymentDeclined(orderId, "do_not_honour");
            await Publish(harness, declined);

            (await Consumed<PaymentDeclined>(harness, m => m.MessageId == declined.MessageId))
                .ShouldBeTrue();

            // Not faulted, and this is the assertion that tells a missing
            // branch from an explicit Ignore; nothing else here would.
            ConsumeFaults<PaymentDeclined>(harness).ShouldAllBe(e => e == null);

            // Nothing escalated: there is no money for a human to chase.
            (await NotYetSent<FlagOrderForReview>(harness, m => m.OrderId == orderId)).ShouldBeFalse();

            // The compensation is untouched and still waiting on Inventory.
            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task A_cancellation_while_compensating_changes_nothing()
    {
        // Compensating already ends in a cancellation, so the request adds
        // nothing to do; it is Ignored explicitly rather than left to
        // OnUnhandledEvent because a reader cannot tell a decision from an
        // omission.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));
            await Publish(harness, SagaContracts.PaymentDeclined(orderId, "do_not_honour"));

            (await Sent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            OrderCancelled cancelled =
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest);
            await Publish(harness, cancelled);

            (await Consumed<OrderCancelled>(harness, m => m.MessageId == cancelled.MessageId))
                .ShouldBeTrue();
            ConsumeFaults<OrderCancelled>(harness).ShouldAllBe(e => e == null);

            // Ignored means the state is untouched, not merely that nothing
            // was sent — the compensation still has to be waiting on Inventory
            // when the release arrives.
            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();

            await Publish(harness, SagaContracts.StockReleased(orderId));

            // payment_declined, not customer_request: the reason recorded on
            // entry is the one that caused the compensation, and a cancellation
            // arriving mid-flight must not rewrite it.
            (await Sent<CancelOrder>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == CancelReasons.PaymentDeclined))
                .ShouldBeTrue();

            harness.Sent
                .Select<ReleaseStock>(Spent())
                .Count(m => m.Context.Message.OrderId == orderId)
                .ShouldBe(1);
        }
    }

    [Fact]
    public async Task A_release_that_overtakes_the_cancellation_is_absorbed_and_the_compensation_still_ends()
    {
        // §3.2 has Inventory consuming OrderCancelled directly, so one
        // publication starts two races to this endpoint: the saga's own copy of
        // the event and the StockReleased Inventory derives from it. Absorbing
        // the early release is safe only because ADR-024 has Inventory answer
        // the saga's own ReleaseStock whatever it already did on the event;
        // otherwise the instance waits out ReleaseTimeout and files a
        // stock_not_released review for stock that came back. So the second
        // publish drives the contract, not merely the branch.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.Exists(orderId, x => x.AwaitingStock)).ShouldNotBeNull();

            StockReleased overtaking = SagaContracts.StockReleased(orderId);
            await Publish(harness, overtaking);

            (await Consumed<StockReleased>(harness, m => m.MessageId == overtaking.MessageId))
                .ShouldBeTrue();

            // Without this branch the arrival faults and spends §9.8's five
            // retries hoping the cancellation lands first.
            ConsumeFaults<StockReleased>(harness).ShouldAllBe(e => e == null);

            // Absorbed rather than acted on — the wait is untouched.
            (await saga.Exists(orderId, x => x.AwaitingStock)).ShouldNotBeNull();

            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest));

            (await Sent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();
            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();

            // ADR-024's first guarantee, driven: Inventory answers the command
            // although it released on the event a moment ago, because
            // StockReleased reports the postcondition rather than a state
            // change.
            await Publish(harness, SagaContracts.StockReleased(orderId));

            (await Sent<CancelOrder>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == CancelReasons.CustomerRequest))
                .ShouldBeTrue();

            // Nothing escalated: the point of the ADR is that this ordinary
            // interleaving does not reach a human.
            (await NotYetSent<FlagOrderForReview>(harness, m => m.OrderId == orderId))
                .ShouldBeFalse();

            (await saga.NotExists(orderId)).ShouldBeNull();
        }
    }

    [Fact]
    public async Task A_release_arriving_after_the_confirmation_is_absorbed_rather_than_faulted()
    {
        // Confirmed's OrderCancelled branch deliberately sends no release, and
        // Inventory releases on the event regardless (§3.2), so the release
        // arrives here too. Elsewhere the retry envelope rescues an early
        // arrival by finding the instance moved on; here the branch finalises,
        // so a second attempt finds no instance and is discarded in silence.
        // What this buys is a clean first delivery, and absorbing loses
        // nothing: the cancellation raises cancelled_after_confirmation on its
        // own branch.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));
            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "PSP-REF-129"));
            await Publish(harness, SagaContracts.OrderConfirmed(orderId, Customer));

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.Exists(orderId, x => x.Confirmed)).ShouldNotBeNull();

            StockReleased released = SagaContracts.StockReleased(orderId);
            await Publish(harness, released);

            (await Consumed<StockReleased>(harness, m => m.MessageId == released.MessageId))
                .ShouldBeTrue();

            ConsumeFaults<StockReleased>(harness).ShouldAllBe(e => e == null);

            // Nothing sent and nothing moved: a despatch is still expected and
            // this event is not evidence against it.
            (await NotYetSent<CancelOrder>(harness, m => m.OrderId == orderId)).ShouldBeFalse();
            (await NotYetSent<FlagOrderForReview>(harness, m => m.OrderId == orderId))
                .ShouldBeFalse();

            (await saga.Exists(orderId, x => x.Confirmed)).ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task A_second_confirmation_in_Confirmed_is_absorbed_rather_than_faulted()
    {
        // The rollout case: a replica that entered Confirmed on the send
        // publishes OrderConfirmed moments after the instance is already there,
        // and the binding is durable and queue-scoped, so the first new replica
        // copies those into the saga queue for instances an old one advanced —
        // §15.5's canary runs both releases for the length of its ladder. The
        // same line covers §9.5's unrecorded redelivery.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            (await Sent<ReserveStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.StockReserved(orderId));
            (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "psp-ref-dup"));
            (await Sent<ConfirmOrder>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            await Publish(harness, SagaContracts.OrderConfirmed(orderId, Customer));
            (await saga.Exists(orderId, x => x.Confirmed)).ShouldNotBeNull();

            // Two facts with two ids, not one object published twice: two
            // deliveries of one message share an id by design, which is §9.5's
            // inbox's problem and not this barrier's.
            OrderConfirmed duplicate = SagaContracts.OrderConfirmed(orderId, Customer);
            await Publish(harness, duplicate);

            (await Consumed<OrderConfirmed>(harness, m => m.MessageId == duplicate.MessageId))
                .ShouldBeTrue();

            ConsumeFaults<OrderConfirmed>(harness).ShouldAllBe(e => e == null);

            (await saga.Exists(orderId, x => x.Confirmed)).ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task A_despatch_that_beats_the_confirmation_still_marks_the_order_shipped()
    {
        // §3.2 gives Shipping OrderConfirmed too, so the aggregate's one
        // publish fans out to two consumers with no ordering between them
        // (§9.4), and the saga's own copy can be behind the despatch. Handled
        // rather than ignored, because ignoring loses MarkOrderShipped; safe,
        // because Shipping learns of the order only from OrderConfirmed, so a
        // despatch arriving proves the confirmation committed.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            (await Sent<ReserveStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.StockReserved(orderId));
            (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "psp-ref-early"));
            (await Sent<ConfirmOrder>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.Exists(orderId, x => x.AwaitingConfirmation)).ShouldNotBeNull();

            // No OrderConfirmed published at all — the despatch arrives first.
            await Publish(harness, SagaContracts.ShipmentDispatched(orderId, "TRACK-EARLY"));

            (await Sent<MarkOrderShipped>(harness, m =>
                m.OrderId == orderId &&
                m.TrackingNumber == "TRACK-EARLY"))
                .ShouldBeTrue();

            ConsumeFaults<ShipmentDispatched>(harness).ShouldAllBe(e => e == null);

            (await saga.NotExists(orderId)).ShouldBeNull();
        }
    }

    [Fact]
    public void The_machine_declares_the_states_the_chapter_draws_and_no_others()
    {
        // §9.6's rule about its own diagram. Cancelled and Shipped are
        // outcomes, not states: SetCompletedWhenFinalized deletes the instance,
        // so either would be a state no saga is ever observed in. Initial and
        // Final are MassTransit's.
        OrderFulfilmentSaga saga = new();

        saga.States
            .Select(s => s.Name)
            .ShouldBe(
                [
                    "Initial",
                    "Final",
                    "AwaitingStock",
                    "AwaitingPayment",
                    "AwaitingConfirmation",
                    "Confirmed",
                    "Compensating"
                ],
                ignoreOrder: true);
    }

    [Fact]
    public void Compensating_writes_out_every_event_it_can_receive()
    {
        // A partition rather than a membership list: a behavioural test can
        // only catch a branch somebody wrote a test for, and an event declared
        // with no Compensating branch faults in production and nowhere in this
        // file. Every declared event is classified below, the two halves must
        // account for all of them, and the reachable half must equal what the
        // machine accepts — so a new event fails the first assertion until
        // classified, and classifying it reachable without the branch fails the
        // second.
        OrderFulfilmentSaga saga = new();

        string[] reachableHere =
        [
            nameof(saga.StockReleased),
            nameof(saga.PaymentAuthorised),
            nameof(saga.PaymentDeclined),
            nameof(saga.OrderCancelled),
            nameof(saga.StockReserved),
            nameof(saga.StockReservationFailed),

            // AwaitingConfirmation is a door into this state, and the only one
            // that can be entered with an OrderConfirmed still outstanding.
            nameof(saga.OrderConfirmed),
            $"{nameof(saga.ReleaseTimeout)}.Received",

            // Cancelling from AwaitingPayment arrives here with Payments still
            // owing a verdict and leaves the wait armed so something bounds the
            // hold; the timeout door re-arms it. This is the exit that ends
            // that wait.
            $"{nameof(saga.PaymentTimeout)}.Received"
        ];

        // Not reachable in Compensating, and each for a stated reason rather
        // than by omission: OrderPlaced only creates an instance,
        // ShipmentDispatched and the despatch timeout belong to Confirmed,
        // and the stock and confirmation timeouts are unscheduled by the
        // transitions that enter this state.
        string[] notReachableHere =
        [
            nameof(saga.OrderPlaced),
            nameof(saga.ShipmentDispatched),
            $"{nameof(saga.StockTimeout)}.Received",
            $"{nameof(saga.ConfirmationTimeout)}.Received",
            $"{nameof(saga.DespatchTimeout)}.Received"
        ];

        string[] declared = DeclaredEvents();

        declared.ShouldNotBeEmpty("the reflection scan found no events on this machine");

        reachableHere
            .Concat(notReachableHere)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ShouldBe(
                declared.OrderBy(n => n, StringComparer.Ordinal),
                "every event this machine declares must be classified as reachable in " +
                "Compensating or not. An event in neither list is one nobody decided about, " +
                "which is exactly how PaymentDeclined came to be missing (§9.6).");

        // .AnyReceived is MassTransit's own, one per Schedule, and it is
        // accepted in every state whether or not anybody wrote a branch — so
        // it says nothing about the subject here and would only dilute it.
        // The schedule itself is still classified, through its .Received.
        saga.NextEvents(saga.Compensating)
            .Select(e => e.Name)
            .Where(n => !n.EndsWith(".AnyReceived", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ShouldBe(
                reachableHere.OrderBy(n => n, StringComparer.Ordinal),
                "Compensating accepts exactly the events classified as reachable there. A name " +
                "missing from the machine is a branch nobody wrote, which now faults in " +
                "production and is caught by no test here; one the machine has and this list " +
                "does not is a branch nobody argued.");
    }

    [Fact]
    public void The_four_states_before_Compensating_write_out_what_they_accept()
    {
        // Per state and hand-written rather than generated, because what makes
        // the claim checkable is naming the events a state can receive and why.
        // This is not a partition: an event declared with no branch here and no
        // entry in a list changes neither side and passes, which is the hole
        // the test below closes from the other end.
        OrderFulfilmentSaga saga = new();

        // AwaitingStock is entered with ReserveStock in flight: Inventory
        // answers either way, the five-minute wait bounds it, and a
        // cancellation compensates. StockReleased is the fourth because
        // Inventory releases on OrderCancelled itself (§3.2), so it can beat
        // the saga's own copy of that event here.
        Accepts(
            saga,
            saga.AwaitingStock,
            [
                nameof(saga.StockReserved),
                nameof(saga.StockReservationFailed),
                nameof(saga.OrderCancelled),
                nameof(saga.StockReleased),
                $"{nameof(saga.StockTimeout)}.Received"
            ]);

        // AwaitingPayment is the same shape one step on: the PSP answers either
        // way, fifteen minutes bounds it, a cancellation compensates, and the
        // derived release can arrive before the cancellation that caused it.
        Accepts(
            saga,
            saga.AwaitingPayment,
            [
                nameof(saga.PaymentAuthorised),
                nameof(saga.PaymentDeclined),
                nameof(saga.OrderCancelled),
                nameof(saga.StockReleased),
                $"{nameof(saga.PaymentTimeout)}.Received"
            ]);

        // AwaitingConfirmation is entered with ConfirmOrder in flight: the
        // acknowledgement ends the wait, a cancellation compensates, the
        // timeout escalates, and a despatch can beat the acknowledgement
        // because Shipping subscribes to the same OrderConfirmed and §9.4
        // orders nothing between two consumers.
        Accepts(
            saga,
            saga.AwaitingConfirmation,
            [
                nameof(saga.OrderConfirmed),
                nameof(saga.OrderCancelled),
                nameof(saga.ShipmentDispatched),
                nameof(saga.StockReleased),
                $"{nameof(saga.ConfirmationTimeout)}.Received"
            ]);

        // Confirmed is entered by OrderConfirmed, so a second one is a
        // duplicate (§9.5's unrecorded redelivery, or a rolling deploy) and is
        // absorbed. StockReleased is here for a different reason from the other
        // three states': they send a release and absorb the early copy of its
        // answer; this state sends none, so the arrival is Inventory acting on
        // the event alone.
        Accepts(
            saga,
            saga.Confirmed,
            [
                nameof(saga.OrderConfirmed),
                nameof(saga.OrderCancelled),
                nameof(saga.ShipmentDispatched),
                nameof(saga.StockReleased),
                $"{nameof(saga.DespatchTimeout)}.Received"
            ]);
    }

    [Fact]
    public void Every_declared_event_is_handled_in_some_state()
    {
        // Both sides are read from the machine — reflection over the declared
        // properties on the left, NextEvents over the declared states on the
        // right — so there is no list to forget and no exemption list; even
        // OrderPlaced is handled, in Initial. Deliberately weaker than a
        // per-state partition: it says an event is handled somewhere, not
        // everywhere it can arrive.
        OrderFulfilmentSaga saga = new();

        string[] declared = DeclaredEvents();
        declared.ShouldNotBeEmpty("the reflection scan found no events on this machine");

        string[] handled =
        [
            .. saga.States
                .SelectMany(saga.NextEvents)
                .Select(e => e.Name)
                .Where(n => !n.EndsWith(".AnyReceived", StringComparison.Ordinal))
                .Distinct()
        ];

        declared
            .Except(handled)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ShouldBeEmpty(
                "every event this machine declares must be receivable in at least one state. " +
                "One that is receivable nowhere is a binding on the saga's queue with no " +
                "transition behind it, which faults on every delivery and reaches the error " +
                "queue once §9.8's five retries are spent.");
    }

    /// <summary>
    /// Every event name the machine declares, including one per
    /// <see cref="Schedule{TInstance, TMessage}"/> in the <c>.Received</c> form
    /// <c>NextEvents</c> reports them under.
    /// </summary>
    /// <remarks>
    /// Extracted rather than copied: two tests classify against this set, and a
    /// second scan that drifted from the first would make one of them quietly
    /// narrower.
    /// </remarks>
    private static string[] DeclaredEvents() =>
    [
        .. typeof(OrderFulfilmentSaga)
            .GetProperties()
            .Where(pi => typeof(Event).IsAssignableFrom(pi.PropertyType))
            .Select(pi => pi.Name),
        .. typeof(OrderFulfilmentSaga)
            .GetProperties()
            .Where(pi => pi.PropertyType.IsGenericType &&
                pi.PropertyType.GetGenericTypeDefinition() == typeof(Schedule<,>))
            .Select(pi => $"{pi.Name}.Received")
    ];

    private static void Accepts(OrderFulfilmentSaga saga, State state, string[] expected) =>
        saga.NextEvents(state)
            .Select(e => e.Name)
            .Where(n => !n.EndsWith(".AnyReceived", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ShouldBe(
                expected.OrderBy(n => n, StringComparer.Ordinal),
                $"{state.Name} accepts exactly the events written out for it. One the machine " +
                "has and this list does not is a branch nobody argued; one this list has and " +
                "the machine does not is an arrival that faults to the error queue.");

    [Fact]
    public void Every_wait_state_declares_a_schedule()
    {
        // Appendix C's "a timeout on every wait state" as a structural claim:
        // the behavioural tests prove each timeout fires, and a wait whose
        // schedule is deleted simply has no test left to go red.
        OrderFulfilmentSaga saga = new();

        object?[] schedules =
        [
            saga.StockTimeout,
            saga.PaymentTimeout,
            saga.ConfirmationTimeout,
            saga.DespatchTimeout,
            saga.ReleaseTimeout
        ];

        schedules.ShouldAllBe(s => s != null);

        // The equality is the guard: a wait state added without a schedule
        // fails here, and so does a schedule left behind by a removed wait
        // state.
        schedules.Length.ShouldBe(saga.States.Count(s => s.Name is not ("Initial" or "Final")));
    }

    [Fact]
    public async Task An_event_for_an_order_with_no_instance_is_discarded_in_silence()
    {
        // MassTransit's policy for a non-initial event correlating to no
        // instance is not the unhandled-event path: the default consumes
        // cleanly and drops it, with nothing on §13.6's pager. That default is
        // what makes OrderCancelled's explicit Discard cheap, and pinning it
        // means an upgrade that changes it is reported here. StockReleased is
        // the subject because ADR-024 has Inventory publish it for every
        // release including a no-op, so reaching a finalised instance is its
        // ordinary case; PaymentAuthorised overrides the default and would
        // measure the override instead.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orphan = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.StockReleased(orphan));

            (await Consumed<StockReleased>(harness, m => m.OrderId == orphan)).ShouldBeTrue();

            ConsumeFaults<StockReleased>(harness).ShouldAllBe(e => e == null);

            // Nothing sent: no transition ran, so the machine never saw the
            // event.
            (await NotYetSent<CancelOrder>(harness, m => m.OrderId == orphan)).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task An_authorisation_for_an_order_with_no_instance_faults_rather_than_vanishing()
    {
        // PaymentVerdictOutstanding holds the instance while a verdict can
        // still arrive; this covers the arrival after the machine stopped
        // waiting. The override is safe on provenance, not timing: Payments
        // produces PaymentAuthorised, so it can never be Ordering's own echo,
        // where OrderCancelled and StockReleased can be and keep the silent
        // default. The fault is what puts the arrival on §13.6's pager.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orphan = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.PaymentAuthorised(orphan, "auth-orphan"));

            (await Consumed<PaymentAuthorised>(harness, m => m.OrderId == orphan)).ShouldBeTrue();

            ConsumeFaults<PaymentAuthorised>(harness).ShouldContain(e => e != null);
        }
    }

    [Fact]
    public async Task A_cancellation_this_workflow_did_not_cause_faults_when_no_instance_exists()
    {
        // A customer's cancellation that overtakes its own OrderPlaced
        // correlates to nothing; consumed cleanly, the placement that follows
        // starts a live saga for a cancelled order. Faulting spends §9.8's
        // retry envelope, about seventy seconds for the OrderPlaced to land,
        // and only then is it an error-queue entry.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orphan = Guid.CreateVersion7();

            await Publish(
                harness,
                SagaContracts.OrderCancelled(
                    orphan,
                    Customer,
                    CancelReasons.CustomerRequest,
                    CancelOrigins.User));

            (await Consumed<OrderCancelled>(harness, m => m.OrderId == orphan)).ShouldBeTrue();

            ConsumeFaults<OrderCancelled>(harness).ShouldContain(e => e != null);
        }
    }

    [Fact]
    public async Task A_cancellation_carrying_this_workflows_own_reason_still_faults_if_it_did_not_cause_it()
    {
        // Reason is not the discriminator: §11.4's endpoint parses all five
        // CancelReasons codes, so a caller may send out_of_stock, and a
        // Reason-based branch would discard this in silence. Origin says User
        // here whatever the reason says.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orphan = Guid.CreateVersion7();

            await Publish(
                harness,
                SagaContracts.OrderCancelled(
                    orphan,
                    Customer,
                    CancelReasons.OutOfStock,
                    CancelOrigins.User));

            (await Consumed<OrderCancelled>(harness, m => m.OrderId == orphan)).ShouldBeTrue();

            ConsumeFaults<OrderCancelled>(harness).ShouldContain(e => e != null);
        }
    }

    [Fact]
    public async Task A_cancellation_carrying_an_unknown_origin_faults_rather_than_being_discarded()
    {
        // What tells an allow-list from a deny-list: null, workflow and user
        // are answered identically by a branch reading "fault only when Origin
        // is user", which would then discard every spelling nobody thought of —
        // a later vocabulary member, a producer on another version, a truncated
        // field.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orphan = Guid.CreateVersion7();

            await Publish(
                harness,
                SagaContracts.OrderCancelled(
                    orphan,
                    Customer,
                    CancelReasons.CustomerRequest,
                    origin: "operations_console"));

            (await Consumed<OrderCancelled>(harness, m => m.OrderId == orphan)).ShouldBeTrue();

            ConsumeFaults<OrderCancelled>(harness).ShouldContain(e => e != null);
        }
    }

    [Fact]
    public async Task A_cancellation_published_before_the_origin_field_existed_is_discarded()
    {
        // A rolling deploy has instances publishing before they populate
        // Origin, and faulting on absent would file an error-queue entry for
        // every cancellation for the length of the deploy. The tolerance is
        // permanent for this contract version: an old payload can arrive from
        // the error queue or a replay long after, and a required Origin would
        // fail deserialisation before this branch ran. This test goes when V1
        // does.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orphan = Guid.CreateVersion7();

            await Publish(
                harness,
                SagaContracts.OrderCancelled(orphan, Customer, CancelReasons.CustomerRequest, origin: null));

            (await Consumed<OrderCancelled>(harness, m => m.OrderId == orphan)).ShouldBeTrue();

            ConsumeFaults<OrderCancelled>(harness).ShouldAllBe(e => e == null);
        }
    }

    [Fact]
    public async Task A_reservation_reported_after_an_early_release_withholds_the_authorisation()
    {
        // A StockReleased in AwaitingStock proves a cancellation reached
        // Inventory (§3.2, ADR-029), so the reservation reported after it has
        // since been released, and authorising a card against it is the harm.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReleased(orderId));
            await Publish(harness, SagaContracts.StockReserved(orderId));

            (await NotYetSent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeFalse();

            // And the compensation still converges: the cancellation that
            // caused the release arrives, this state's own branch releases and
            // waits, and Inventory answers a release of nothing (ADR-024).
            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest));

            (await Sent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.StockReleased(orderId));

            (await Sent<CancelOrder>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == CancelReasons.CustomerRequest))
                .ShouldBeTrue();

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.NotExists(orderId)).ShouldBeNull();
        }
    }

    [Fact]
    public async Task An_authorisation_after_an_early_release_escalates_rather_than_confirming()
    {
        // Without the guard PaymentAuthorised confirms an order the customer
        // cancelled, and consumes the one arrival that raises
        // payment_authorised_during_compensation.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));
            await Publish(harness, SagaContracts.StockReleased(orderId));
            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "auth-late"));

            (await Sent<FlagOrderForReview>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == ReviewReasons.PaymentAuthorisedDuringCompensation))
                .ShouldBeTrue();

            (await NotYetSent<ConfirmOrder>(harness, m => m.OrderId == orderId)).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task A_confirmation_after_an_early_release_escalates_on_its_way_to_Confirmed()
    {
        // A StockReleased in AwaitingConfirmation records the cancellation; the
        // OrderConfirmed that follows would otherwise arm a three-day despatch
        // wait whose expiry raises not_despatched, and nothing would ever say
        // the order was cancelled. The transition still happens — the aggregate
        // committed the status, so the machine may not claim a state the order
        // has left — and the guard adds the row.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));
            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "auth-3"));
            await Publish(harness, SagaContracts.StockReleased(orderId));
            await Publish(harness, SagaContracts.OrderConfirmed(orderId, Customer));

            (await Sent<FlagOrderForReview>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == ReviewReasons.CancelledAfterConfirmation))
                .ShouldBeTrue();

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.Exists(orderId, x => x.Confirmed)).ShouldNotBeNull(
                "the confirmation is a fact and the machine still records it");
        }
    }

    [Fact]
    public async Task A_despatch_after_an_early_release_marks_the_order_shipped_and_escalates()
    {
        // Confirmed's ShipmentDispatched finalises, so a cancellation in flight
        // then reaches a deleted instance and nothing records it.
        // MarkOrderShipped still goes and the aggregate refuses it: the flag is
        // set only by a StockReleased published off an OrderCancelled staged in
        // the cancelling transaction (ADR-029), so MarkOrderShippedHandler
        // answers order.not_shippable. The assertion is that the command is
        // sent — §5.4 gives the aggregate the transition — not that the order
        // records a despatch, which this harness could not see.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));
            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "auth-1"));
            await Publish(harness, SagaContracts.OrderConfirmed(orderId, Customer));
            await Publish(harness, SagaContracts.StockReleased(orderId));
            await Publish(harness, SagaContracts.ShipmentDispatched(orderId, "TRK-9"));

            (await Sent<MarkOrderShipped>(harness, m =>
                m.OrderId == orderId &&
                m.TrackingNumber == "TRK-9"))
                .ShouldBeTrue();

            (await Sent<FlagOrderForReview>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == ReviewReasons.CancelledAfterConfirmation))
                .ShouldBeTrue();

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.NotExists(orderId)).ShouldBeNull();
        }
    }

    [Fact]
    public async Task A_despatch_beating_the_confirmation_after_an_early_release_escalates_too()
    {
        // The same interleaving one state earlier: §3.2 gives Shipping
        // OrderConfirmed too, so a despatch can reach this saga before its own
        // acknowledgement, and that branch finalises as well.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));
            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "auth-2"));
            await Publish(harness, SagaContracts.StockReleased(orderId));
            await Publish(harness, SagaContracts.ShipmentDispatched(orderId, "TRK-10"));

            (await Sent<MarkOrderShipped>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            (await Sent<FlagOrderForReview>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == ReviewReasons.CancelledAfterConfirmation))
                .ShouldBeTrue();
        }
    }

    [Fact]
    public async Task A_publish_returns_only_after_the_saga_has_consumed_that_message()
    {
        // The subject is the barrier, not the saga: every test that drives the
        // saga stays green with the barrier removed, so a barrier is only ever
        // observed working unless something looks at it. "Consumed", not
        // "transitioned": the guarantee is that the saga took delivery, and the
        // second half of this test is a StockReserved in AwaitingPayment that
        // runs nothing at all. Read as of now on a spent token: if Publish
        // returned early, the command Initially sends would not be recorded
        // yet. Against a type-level wait only the second assertion fails, and
        // only usually, which is why the gated test below exists.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));

            harness.Sent
                .Select<ReserveStock>(Spent())
                .Count(m => m.Context.Message.OrderId == orderId)
                .ShouldBe(1);

            // Two StockReserved facts, not one object published twice: for a
            // contract Publish writes the envelope onto the send context, so
            // two SagaContracts calls are two ids the fence can tell apart,
            // while one object twice would put one id on the wire twice —
            // §9.5's inbox's problem, and a residual this barrier cannot
            // separate. A type-level wait fails on the count below: it is
            // satisfied by the first delivery with the second still in flight.
            StockReserved first = SagaContracts.StockReserved(orderId);
            StockReserved second = SagaContracts.StockReserved(orderId);
            await Publish(harness, first);
            await Publish(harness, second);

            harness.Consumed
                .Select<StockReserved>(Spent())
                .Count(m => m.Context.Message.OrderId == orderId)
                .ShouldBe(2);

            // The second delivery lands in AwaitingPayment, which declares no
            // StockReserved branch, so it faults. Stated, because a test
            // quietly pushing a message onto the error queue is what §13.6
            // pages on, and because exactly one fault proves two distinct
            // deliveries each reached the machine rather than one counted
            // twice.
            ConsumeFaults<StockReserved>(harness)
                .Count(e => e != null)
                .ShouldBe(1);
        }
    }

    /// <summary>
    /// A consumer that does not return until the test lets it, so "did
    /// <see cref="Publish"/> wait?" has an answer that does not depend on
    /// timing.
    /// </summary>
    /// <remarks>
    /// The saga cannot be this consumer: its transitions return immediately, so
    /// any question about the barrier asked through it is answered by whichever
    /// of two fast operations finished first.
    /// </remarks>
    private sealed class GateConsumer : IConsumer<GateProbe>
    {
        internal static TaskCompletionSource Arrived { get; private set; } = new();

        internal static TaskCompletionSource Release { get; private set; } = new();

        internal static void Reset()
        {
            Arrived = new TaskCompletionSource();
            Release = new TaskCompletionSource();
        }

        public async Task Consume(ConsumeContext<GateProbe> context)
        {
            Arrived.TrySetResult();
            await Release.Task;
        }
    }

    /// <summary>
    /// Not an <c>IIntegrationEvent</c>, deliberately: it never crosses a
    /// service boundary, so §4.3 and §9.1 have nothing to say about it, and
    /// giving it an envelope would only add a second thing to keep true.
    /// </summary>
    private sealed record GateProbe(Guid Id);

    [Fact]
    public async Task A_publish_does_not_return_while_its_own_message_is_still_being_consumed()
    {
        // The deterministic half: the test above can observe the barrier only
        // through a race, because the saga's transitions return at once. With
        // the consumer held open both halves are settled by construction:
        //
        //   1. Publish must not return while its message is unconsumed.
        //   2. It must not be satisfied by a different message of the same
        //      type, which is exactly what a type-level wait does.
        GateConsumer.Reset();

        ServiceProvider provider = new ServiceCollection()
            .AddMassTransitTestHarness(x =>
            {
                x.SetTestTimeouts(TestTimeout, InactivityTimeout);
                x.AddConsumer<GateConsumer>();
                x.UsingInMemory((context, cfg) => cfg.ConfigureEndpoints(context));
            })
            .BuildServiceProvider(true);

        await using (provider)
        {
            ITestHarness harness = provider.GetRequiredService<ITestHarness>();
            await harness.Start();

            // The release goes in a finally: without one a failing assertion
            // leaves the consumer blocked, the harness never drains, disposal
            // never returns, and the run hangs instead of going red.
            Task publish = Publish(harness, new GateProbe(Guid.CreateVersion7()));
            try
            {
                // Nothing here waits on a clock: the barrier is either open or
                // it is not. Bounded for the reason the finally exists — if the
                // publish faulted or the probe never routed, an unbounded await
                // hangs the run rather than failing it.
                await GateConsumer.Arrived.Task
                    .WaitAsync(InactivityTimeout, TestContext.Current.CancellationToken);
                publish.IsCompleted.ShouldBeFalse(
                    "Publish returned while its own message was still inside the consumer, " +
                    "so it is not a barrier at all.");
            }
            finally
            {
                GateConsumer.Release.TrySetResult();
            }

            await publish;

            // The second half: the first probe is consumed, so a type-level
            // wait is already satisfied, while an id-level wait cannot be
            // because this message has not been delivered yet.
            GateConsumer.Reset();

            Task second = Publish(harness, new GateProbe(Guid.CreateVersion7()));
            try
            {
                await GateConsumer.Arrived.Task
                    .WaitAsync(InactivityTimeout, TestContext.Current.CancellationToken);
                second.IsCompleted.ShouldBeFalse(
                    "Publish returned once SOME message of the type had been consumed rather " +
                    "than its own — which is the type-level wait, and it fences nothing.");
            }
            finally
            {
                GateConsumer.Release.TrySetResult();
            }

            await second;
        }
    }
}
