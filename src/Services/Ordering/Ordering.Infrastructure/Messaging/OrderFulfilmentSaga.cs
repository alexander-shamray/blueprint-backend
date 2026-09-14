using Common.Contracts.Inventory.V1;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Payments.V1;
using Common.Contracts.Shipping.V1;
using MassTransit;
using static Ordering.Infrastructure.Messaging.Endpoints;

namespace Ordering.Infrastructure.Messaging;

/// <summary>
/// §9.6's order fulfilment saga: the workflow across Inventory, Payments and
/// Shipping, coordinated without a distributed transaction. Each forward step
/// has a compensating action and every wait has a timeout.
/// </summary>
/// <remarks>
/// Commands are sent, never published, so exactly one owning service executes
/// them (§9.6). The class is public because MassTransit's registration
/// resolves it from the container by type; nothing else in this assembly
/// names it.
/// </remarks>
public sealed class OrderFulfilmentSaga : MassTransitStateMachine<OrderFulfilmentState>
{
    /// <summary>
    /// How long §9.6 waits for Inventory to answer <c>ReserveStock</c>.
    /// </summary>
    /// <remarks>
    /// Each wait's delay is named here and read by the <c>Schedule</c> that
    /// arms it, so the value has one home; §9.6's diagram names the waits and
    /// prints no durations. The argument for a value stays at the schedule
    /// that arms it, and one delay per wait state is the invariant a
    /// structural test guards.
    /// </remarks>
    public static readonly TimeSpan StockTimeoutDelay = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long §9.6 waits for Payments to return a verdict. Longer than
    /// <see cref="StockTimeoutDelay"/> because a PSP retry is normal.
    /// </summary>
    /// <inheritdoc cref="StockTimeoutDelay" path="/remarks"/>
    public static readonly TimeSpan PaymentTimeoutDelay = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long §9.6 waits for this service's own <c>ConfirmOrder</c> to be
    /// acknowledged.
    /// </summary>
    /// <inheritdoc cref="StockTimeoutDelay" path="/remarks"/>
    public static readonly TimeSpan ConfirmationTimeoutDelay = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long §9.6 waits for a <c>ReleaseStock</c> it sent while
    /// compensating. Matches <see cref="ConfirmationTimeoutDelay"/>, and for
    /// the same reason: both bound a message this service has already sent
    /// rather than a third party deciding something.
    /// </summary>
    /// <inheritdoc cref="StockTimeoutDelay" path="/remarks"/>
    public static readonly TimeSpan ReleaseTimeoutDelay = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long §9.6 waits for despatch once an order is confirmed. Days
    /// rather than minutes: the far end is a warehouse.
    /// </summary>
    /// <inheritdoc cref="StockTimeoutDelay" path="/remarks"/>
    public static readonly TimeSpan DespatchTimeoutDelay = TimeSpan.FromDays(3);

    // Every state in §9.6's diagram. Confirmed exists because the order is
    // not done at payment — it is waiting for despatch, and a wait the machine
    // cannot represent is a wait it cannot time out. AwaitingConfirmation
    // exists because Confirmed means the aggregate has confirmed and Shipping
    // knows, which is only true once its own OrderConfirmed has arrived, not
    // when ConfirmOrder is sent.
    //
    // Cancelled and Shipped are not states: they are terminal outcomes, and
    // SetCompletedWhenFinalized() deletes the instance at that point, so a
    // state for either would be one no saga is ever observed in.
    public State AwaitingStock { get; private set; } = null!;
    public State AwaitingPayment { get; private set; } = null!;
    public State AwaitingConfirmation { get; private set; } = null!;
    public State Confirmed { get; private set; } = null!;
    public State Compensating { get; private set; } = null!;

    public Event<OrderPlaced> OrderPlaced { get; private set; } = null!;
    public Event<StockReserved> StockReserved { get; private set; } = null!;
    public Event<StockReservationFailed> StockReservationFailed { get; private set; } = null!;
    public Event<PaymentAuthorised> PaymentAuthorised { get; private set; } = null!;
    public Event<PaymentDeclined> PaymentDeclined { get; private set; } = null!;
    public Event<StockReleased> StockReleased { get; private set; } = null!;
    public Event<ShipmentDispatched> ShipmentDispatched { get; private set; } = null!;

    // Ordering's own event, as OrderPlaced above and OrderConfirmed below are:
    // §3.2 makes a service a subscriber to itself whenever a fact it publishes
    // is one its workflow has to react to. §11.4's customer endpoint cancels
    // the aggregate, and without this binding the saga would go on reserving
    // stock and authorising a card for an order already cancelled.
    //
    // Binding a new event to an existing queue has a rollout cost: while two
    // releases consume ordering-fulfilment-saga, the broker can hand it to an
    // old replica whose machine does not declare it, and MassTransit moves a
    // message an endpoint has no consumer for to <queue>_skipped. §13.6 pages
    // on that queue, and §9.2's rule — consumer capability ships ahead of the
    // producer, or the release cuts over without overlap (ADR-026, §15.5) — is
    // what keeps it empty.
    public Event<OrderCancelled> OrderCancelled { get; private set; } = null!;

    // The acknowledgement AwaitingConfirmation waits for. It costs no contract
    // change: Order.ConfirmPayment raises OrderConfirmedDomainEvent, §9.3's
    // mapper stages OrderConfirmed on the outbox in the transaction that sets
    // the status, and Shipping already binds it (§3.2).
    //
    // The rollout cost is the one stated on OrderCancelled above, with two
    // further directions. An instance an old replica advanced and a new one
    // is handed the acknowledgement for is absorbed by Confirmed's
    // Ignore(OrderConfirmed). An old replica handed any event for an instance
    // whose CurrentState reads AwaitingConfirmation throws
    // UnknownStateException before any branch runs, because MassTransit
    // resolves the state name against the machine it has; only draining or a
    // non-overlapping cutover (§15.5) closes that.
    public Event<OrderConfirmed> OrderConfirmed { get; private set; } = null!;

    // One schedule per wait. "Every wait has a timeout" is a rule the machine
    // must be able to express, not a habit to remember at each transition.
    public Schedule<OrderFulfilmentState, StockReservationExpired> StockTimeout { get; private set; } = null!;
    public Schedule<OrderFulfilmentState, PaymentAuthorisationExpired> PaymentTimeout { get; private set; } = null!;
    public Schedule<OrderFulfilmentState, ConfirmationExpired> ConfirmationTimeout { get; private set; } = null!;
    public Schedule<OrderFulfilmentState, DespatchExpired> DespatchTimeout { get; private set; } = null!;
    public Schedule<OrderFulfilmentState, StockReleaseExpired> ReleaseTimeout { get; private set; } = null!;

    public OrderFulfilmentSaga()
    {
        InstanceState(x => x.CurrentState);

        // Nothing catches an unhandled event, by decision. MassTransit's
        // default raises UnhandledEventException, so an event reaching a state
        // with no transition for it spends §9.8's retries and lands in the
        // error queue §13.6 pages on.
        //
        // A catch-all Ignore would answer a misroute — a configuration fault
        // that wants to be loud — the same way as a duplicate, and nothing
        // here can tell them apart. Two arrivals are possible: §9.5's inbox
        // suppresses the ordinary redelivery, but InboxFilter writes its row
        // after the inner pipe returns, so a crash in that window leaves the
        // next delivery to find an instance that has moved on; and ADR-032's
        // Entity Framework outbox commits the sends in the saga's own
        // transaction, so such a redelivery is only ever a duplicate, never
        // evidence of a loss.
        //
        // What replaces the catch-all is enumeration: every event legitimately
        // arriving in a state with no work for it is written out, with an
        // Ignore where nothing is learnt and a recording branch where
        // something is, and a structural test partitions the declared
        // next-events into reachable and not. An unenumerated arrival is a
        // fault by design.

        // Correlated on the order in every case, which is also what §9.3's
        // mapper sets CorrelationId to — so one id follows the workflow across
        // every service that touches it.
        Event(() => OrderPlaced, x => x.CorrelateById(m => m.Message.OrderId));
        Event(() => StockReserved, x => x.CorrelateById(m => m.Message.OrderId));
        Event(() => StockReservationFailed, x => x.CorrelateById(m => m.Message.OrderId));

        // The one event whose missing instance is always a fault: Payments
        // produces it, so it can never be this service's own echo, and every
        // state that can receive one has a transition for it — so an
        // authorisation with no instance means the machine stopped waiting
        // while Payments was still going to answer, and money moved on an
        // order this saga cancelled. Faulting lands it in the error queue
        // §13.6 pages on, message retained, instead of MassTransit's default
        // clean discard. It spends §9.8's retries first, which cannot help;
        // excluding them would need the retry filter to name a MassTransit
        // exception type, a second reason for this endpoint's ladder to differ.
        Event(
            () => PaymentAuthorised,
            x =>
            {
                x.CorrelateById(m => m.Message.OrderId);
                x.OnMissingInstance(m => m.Fault());
            });

        Event(() => PaymentDeclined, x => x.CorrelateById(m => m.Message.OrderId));
        Event(() => StockReleased, x => x.CorrelateById(m => m.Message.OrderId));
        Event(() => ShipmentDispatched, x => x.CorrelateById(m => m.Message.OrderId));
        Event(() => OrderConfirmed, x => x.CorrelateById(m => m.Message.OrderId));

        // Discarded when no instance exists only for the arrivals this service
        // can account for, and faulted otherwise. MassTransit's default consumes
        // a non-initial event with no instance cleanly, which StockReleased
        // keeps; here it would also swallow a customer cancellation overtaking
        // its own OrderPlaced — §9.4 orders nothing, and the later placement
        // would then start a live saga for an order the aggregate had already
        // cancelled.
        //
        // Reason cannot discriminate: §11.4's endpoint accepts the whole
        // CancellationReasons map, so it says what somebody asserted and
        // nothing about where the request came from. Origin can, because the
        // handler writes it from CommandOrigin — a literal at the entry point,
        // never bound from a request — and §9.2 makes a new optional field
        // additive rather than a V2.
        //
        // An allow-list, because a deny-list passes every spelling nobody
        // thought of. workflow is the echo: the aggregate's OrderCancelled
        // after a CancelOrder this saga sent, arriving after the instance was
        // deleted. absent is a payload published before the field existed,
        // tolerated permanently: a retained message can survive indefinitely,
        // and requiring Origin would fail deserialisation inside V1, which
        // §9.2 sends to a V2. A user origin faults, and §9.8's retries give an
        // OrderPlaced still in flight time to create the instance; if it never
        // does, the message reaches the error queue §13.6 pages on instead of
        // vanishing.
        Event(
            () => OrderCancelled,
            x =>
            {
                x.CorrelateById(m => m.Message.OrderId);
                x.OnMissingInstance(m => m.ExecuteAsync(NoInstanceForCancellation));
            });

        Schedule(
            () => StockTimeout,
            x => x.StockTimeoutTokenId,
            s =>
            {
                s.Delay = StockTimeoutDelay;
                s.Received = e => e.CorrelateById(m => m.Message.OrderId);
            });

        // Payment authorisation involves a third party and is the wait most
        // likely to hang. Longer than stock because a PSP retry is normal.
        Schedule(
            () => PaymentTimeout,
            x => x.PaymentTimeoutTokenId,
            s =>
            {
                s.Delay = PaymentTimeoutDelay;
                s.Received = e => e.CorrelateById(m => m.Message.OrderId);
            });

        // The only wait whose far end is this same service, so the delay is a
        // bound on this repository's own mechanisms. §9.8's retry ladder on
        // ordering-commands is a floor it clears easily; what decides it is
        // §9.4's dispatcher backoff (OutboxDispatcher's BackoffBaseSeconds
        // doubling to BackoffAttemptCap), which overtakes this delay within a
        // few attempts. A publish that succeeds late in that ladder lands after
        // this has fired, filing a not_confirmed review for an order that then
        // confirms — accepted, because that many failures is a stuck outbox
        // §13.6's abandoned-row alert exists to catch, and outlasting the whole
        // ladder would trade a rare false escalation for a common silent one.
        //
        // Like DespatchTimeout it escalates rather than compensating: the card
        // is authorised by the time this wait begins and §3.2 gives Ordering no
        // refund command, so there is no automatic action left.
        Schedule(
            () => ConfirmationTimeout,
            x => x.ConfirmationTimeoutTokenId,
            s =>
            {
                s.Delay = ConfirmationTimeoutDelay;
                s.Received = e => e.CorrelateById(m => m.Message.OrderId);
            });

        // Despatch is measured in days, and unlike the compensating waits it
        // has no automatic compensation — payment is taken and stock is gone.
        // The timeout escalates to a human instead. A wait with no compensating
        // action still needs a bound; "no timeout" is not the alternative.
        Schedule(
            () => DespatchTimeout,
            x => x.DespatchTimeoutTokenId,
            s =>
            {
                s.Delay = DespatchTimeoutDelay;
                s.Received = e => e.CorrelateById(m => m.Message.OrderId);
            });

        // Compensation is a wait like any other. Stock that is never released
        // is stock nobody can sell, and a saga stuck mid-compensation is the
        // worst place to be stuck — the order is already failing.
        Schedule(
            () => ReleaseTimeout,
            x => x.ReleaseTimeoutTokenId,
            s =>
            {
                s.Delay = ReleaseTimeoutDelay;
                s.Received = e => e.CorrelateById(m => m.Message.OrderId);
            });

        Initially(
            When(OrderPlaced)
                .Then(ctx =>
                {
                    ctx.Saga.OrderId = ctx.Message.OrderId;
                    ctx.Saga.Total = ctx.Message.TotalAmount;
                    ctx.Saga.Currency = ctx.Message.Currency;
                    ctx.Saga.StartedAt = ctx.Message.OccurredAt;
                })
                .Schedule(StockTimeout, ctx => new StockReservationExpired(ctx.Saga.OrderId))
                // Send, not Publish — these are commands with one owner.
                // Mapped, not forwarded: ReserveStock owns its line type, so
                // versioning OrderPlaced does not version Inventory's command.
                .Send(
                    InventoryQueue,
                    ctx => new ReserveStock(
                        ctx.Saga.OrderId,
                        [.. ctx.Message.Lines.Select(l => new StockLine(l.ProductId, l.Quantity))]))
                .TransitionTo(AwaitingStock));

        During(
            AwaitingStock,
            // The forward step is conditional on the money. A StockReleased
            // recorded below proves a cancellation reached Inventory, so a
            // reservation reported after it has already been released, and
            // authorising a card against it is what the guard refuses.
            //
            // The observed branch sends nothing, moves nowhere and does not
            // Unschedule either: the OrderCancelled behind the release is in
            // flight and this state's branch for it compensates properly, so
            // the instance waits where that branch can still be reached.
            // StockTimeout stays armed to bound that wait — a cancellation with
            // the saga's own reason against an order the aggregate has already
            // cancelled, which Order.Cancel absorbs idempotently (§5.4).
            When(StockReserved)
                .If(
                    ctx => !ctx.Saga.CancellationObserved,
                    proceed => proceed
                        .Unschedule(StockTimeout)
                        // Recorded before the command is sent: from here until a
                        // verdict lands Payments owes this saga an answer, and
                        // Compensating refuses to finalise while it does, so the
                        // obligation is on the instance that commits with this
                        // transition rather than inferred later from the state.
                        .Then(ctx => ctx.Saga.PaymentVerdictOutstanding = true)
                        // Currency travels with the amount — a bare decimal is a
                        // charge waiting to be made in the wrong denomination. No
                        // subject travels with either (ADR-028): Payments derives
                        // the payer from its own record of the order, built from
                        // the OrderPlaced it consumes (§3.2), and a customer
                        // identifier here would be a second source for an
                        // authority the receiver already holds.
                        .Send(
                            PaymentsQueue,
                            ctx => new AuthorisePayment(
                                ctx.Saga.OrderId,
                                ctx.Saga.Total,
                                ctx.Saga.Currency))
                        // Arm the next wait in the same activity that begins it.
                        .Schedule(PaymentTimeout, ctx => new PaymentAuthorisationExpired(ctx.Saga.OrderId))
                        .TransitionTo(AwaitingPayment)),

            When(StockReservationFailed)
                .Unschedule(StockTimeout)
                // String codes, not the domain enum — a published contract
                // carrying one pins its member names as wire format (§9.6).
                .Send(
                    OrderingQueue,
                    ctx => new CancelOrder(ctx.Saga.OrderId, CancelReasons.OutOfStock))
                .Finalize(),

            When(StockTimeout.Received)
                .Send(
                    OrderingQueue,
                    ctx => new CancelOrder(ctx.Saga.OrderId, CancelReasons.StockTimeout))
                .Finalize(),

            // The customer cancelled while ReserveStock was in flight. Nothing
            // has been charged and the reservation may or may not exist yet, so
            // this compensates rather than finalising: it is the same situation
            // as a declined payment, and a ReleaseStock followed by Finalize
            // would lose the wait §9.6 gives compensation a timeout for.
            //
            // One OrderCancelled starts two races to this endpoint. §3.2 has
            // Inventory consuming it directly and publishing StockReleased, so
            // a release derived from this very event can reach the saga before
            // the saga's own copy. AwaitingStock, AwaitingPayment,
            // AwaitingConfirmation and Confirmed can each be holding the
            // instance when it does, and each writes the arrival out as a
            // recording branch. Absorbing it is sound only because ADR-024 has
            // Inventory answer the ReleaseStock sent below whether or not it
            // already released on the event, so Compensating's exit does not
            // depend on the copy that was absorbed.
            When(OrderCancelled)
                .Unschedule(StockTimeout)
                // The event's reason, not a literal: §11.4 accepts the whole
                // CancellationReasons map, so a literal would overwrite whatever
                // the aggregate reported. The decline and timeout branches use
                // literals because those transitions are the decline and the
                // timeout; this one is whatever arrived, and CancelOrderMapper
                // refuses an unknown code on the way back through.
                .Then(ctx => ctx.Saga.CancelReason = ctx.Message.Reason)
                .Send(InventoryQueue, ctx => new ReleaseStock(ctx.Saga.OrderId))
                .Schedule(ReleaseTimeout, ctx => new StockReleaseExpired(ctx.Saga.OrderId))
                .TransitionTo(Compensating),

            // Inventory's release, derived from the cancellation this state has
            // not consumed yet. Left unwritten it faults, and under a backlog
            // that outlasts §9.8's retries the release lands in the error queue
            // while the instance waits out ReleaseTimeout and files a
            // stock_not_released review for stock that came back. Every
            // producer of StockReleased is cancellation-derived (§3.2, ADR-024:
            // a ReleaseStock, consuming OrderCancelled directly, or a
            // ReserveStock refused against a tombstone), and this state has
            // sent no release, so an arrival is always a cancellation this saga
            // is about to consume on its own copy.
            //
            // Recorded rather than ignored, because the release is the only
            // evidence a cancellation gives this machine before its own copy
            // lands, and StockReserved above sends AuthorisePayment unless it
            // is told.
            When(StockReleased)
                .Then(ctx => ctx.Saga.CancellationObserved = true));

        During(
            AwaitingPayment,
            // This activity sends ConfirmOrder; it does not confirm the order.
            // The state and the despatch wait both begin on the aggregate's own
            // OrderConfirmed, the first moment either claim is true. Not
            // Finalize either way: the order is not finished at payment.
            When(PaymentAuthorised)
                // Cleared before the branch because both arms are an answer to
                // the verdict Payments owed; the timeout below merely stops
                // asking.
                .Then(ctx => ctx.Saga.PaymentVerdictOutstanding = false)
                // A cancellation observed here changes what an authorisation
                // means: confirming would turn a verdict that arrived after the
                // customer cancelled into a confirmed order, and consume the one
                // arrival that raises payment_authorised_during_compensation.
                // So it escalates and stays — §3.2 gives Ordering no refund
                // command, so a row for a person is all this machine can do
                // about money that has moved — and the cancellation in flight
                // compensates through this state's own OrderCancelled branch.
                //
                // PaymentTimeout is deliberately not unscheduled on that arm.
                // ADR-021's scheduler cannot cancel, so Unschedule only clears
                // the token that makes the expiry handled; leaving it set keeps
                // a bound on an instance whose OrderCancelled never arrives, and
                // this state's timeout branch compensates as that one would.
                .IfElse(
                    ctx => ctx.Saga.CancellationObserved,
                    observed => observed
                        .Send(
                            OrderingQueue,
                            ctx => new FlagOrderForReview(
                                ctx.Saga.OrderId,
                                ReviewReasons.PaymentAuthorisedDuringCompensation)),
                    proceed => proceed
                        .Unschedule(PaymentTimeout)
                        .Send(
                            OrderingQueue,
                            ctx => new ConfirmOrder(ctx.Saga.OrderId, ctx.Message.Reference))
                        .Schedule(ConfirmationTimeout, ctx => new ConfirmationExpired(ctx.Saga.OrderId))
                        .TransitionTo(AwaitingConfirmation)),

            When(PaymentDeclined)
                .Unschedule(PaymentTimeout)
                .Then(ctx => ctx.Saga.PaymentVerdictOutstanding = false)
                // Recorded on entry because Compensating's exits are shared, and
                // by the time one runs the triggering event is gone: the reason
                // has to be state, not re-derived from the finishing transition.
                .Then(ctx => ctx.Saga.CancelReason = CancelReasons.PaymentDeclined)
                .Send(InventoryQueue, ctx => new ReleaseStock(ctx.Saga.OrderId))
                .Schedule(ReleaseTimeout, ctx => new StockReleaseExpired(ctx.Saga.OrderId))
                .TransitionTo(Compensating),

            When(PaymentTimeout.Received)
                // Same compensation as a decline, not the same reason: collapsing
                // the pair would make the PSP going quiet indistinguishable from
                // customers being declined on the one dashboard that asks.
                .Then(ctx => ctx.Saga.CancelReason = CancelReasons.PaymentTimeout)
                .Send(InventoryQueue, ctx => new ReleaseStock(ctx.Saga.OrderId))
                .Schedule(ReleaseTimeout, ctx => new StockReleaseExpired(ctx.Saga.OrderId))
                // PaymentVerdictOutstanding is left set and the wait is armed a
                // second time. A PSP that has not answered has not declined; the
                // authorisation it may still complete is what
                // payment_authorised_during_compensation is for, and without a
                // live token no expiry can ever reach Compensating to bound the
                // hold. One further window and not more: thirty minutes is the
                // whole hold on a cancelled order, and Compensating's own
                // timeout branch stops asking for good.
                .Schedule(PaymentTimeout, ctx => new PaymentAuthorisationExpired(ctx.Saga.OrderId))
                .TransitionTo(Compensating),

            // Stock is held and AuthorisePayment has already been sent, so this
            // compensates on the decline branch's terms. It does not stop a
            // charge: the request is with Payments, and §3.2 has that service
            // consuming OrderCancelled without saying it voids an authorisation
            // in flight. What this saga guarantees is narrower — no further
            // AuthorisePayment, and one authorised anyway is escalated by
            // Compensating.
            //
            // The payment wait is not unscheduled here, and that absence is the
            // load-bearing part. Every other exit from this state has the
            // verdict or has stopped wanting it; this one cancels while Payments
            // still owes an answer, so the wait runs on into Compensating, which
            // receives it. ADR-021's scheduler cannot recall a delayed message
            // anyway: Unschedule clears the token, and clearing it would discard
            // the one arrival that bounds how long Compensating holds the
            // instance open for a verdict.
            When(OrderCancelled)
                // The event's reason, for the argument on the AwaitingStock
                // branch above.
                .Then(ctx => ctx.Saga.CancelReason = ctx.Message.Reason)
                .Send(InventoryQueue, ctx => new ReleaseStock(ctx.Saga.OrderId))
                .Schedule(ReleaseTimeout, ctx => new StockReleaseExpired(ctx.Saga.OrderId))
                .TransitionTo(Compensating),

            // The early release again, on AwaitingStock's argument: this state's
            // OrderCancelled branch sends its own ReleaseStock and ADR-024 has
            // it answered. What differs is that a verdict is also in flight,
            // and that is Compensating's problem on either arrival order.
            // Recorded because PaymentAuthorised can win the next lock, and
            // unguarded it would confirm an order the customer has cancelled
            // and consume the verdict that should have raised
            // payment_authorised_during_compensation.
            When(StockReleased)
                .Then(ctx => ctx.Saga.CancellationObserved = true));

        // ConfirmOrder is in flight and nothing downstream knows anything yet:
        // the aggregate is still AwaitingPayment, no OrderConfirmed has been
        // published, and Shipping has not been told. Every branch below turns
        // on that being true.
        During(
            AwaitingConfirmation,
            // The acknowledgement: the aggregate committed the status and staged
            // this event in the same transaction (§6.3), so this is the first
            // moment a despatch can be expected, which is why DespatchTimeout is
            // armed here rather than one state back.
            //
            // A confirmation after an observed cancellation raises the row on
            // the way through and still transitions. The confirmation is a fact,
            // so withholding the move would leave the machine claiming a state
            // the order has left; what the flag adds is that Shipping has been
            // told after a cancellation reached Inventory — the evidence
            // Compensating's own When(OrderConfirmed) escalates on. Unguarded, a
            // DespatchTimeout firing from Confirmed would raise not_despatched
            // and nothing would ever have recorded the cancellation.
            When(OrderConfirmed)
                .Unschedule(ConfirmationTimeout)
                .If(
                    ctx => ctx.Saga.CancellationObserved,
                    cancelled => cancelled
                        .Send(
                            OrderingQueue,
                            ctx => new FlagOrderForReview(
                                ctx.Saga.OrderId,
                                ReviewReasons.CancelledAfterConfirmation)))
                .Schedule(DespatchTimeout, ctx => new DespatchExpired(ctx.Saga.OrderId))
                .TransitionTo(Confirmed),

            // The release is unambiguously right here: no OrderConfirmed has
            // been seen, so Shipping was never told and nothing is being picked.
            // It escalates nothing, unlike Confirmed's branch: the money is
            // authorised, but §3.2 has Payments void on OrderCancelled itself,
            // and what makes the confirmed case a human's problem is a despatch
            // that might be moving. The residual it cannot see is a confirmation
            // committed a moment before the cancellation; that OrderConfirmed
            // then arrives in Compensating, which raises
            // cancelled_after_confirmation rather than absorbing it.
            When(OrderCancelled)
                .Unschedule(ConfirmationTimeout)
                .Then(ctx => ctx.Saga.CancelReason = ctx.Message.Reason)
                .Send(InventoryQueue, ctx => new ReleaseStock(ctx.Saga.OrderId))
                .Schedule(ReleaseTimeout, ctx => new StockReleaseExpired(ctx.Saga.OrderId))
                .TransitionTo(Compensating),

            // No acknowledgement and no cancellation, so the machine is out of
            // moves: the card is authorised, the stock is held, and §3.2 gives
            // Ordering no refund command. Reaching this is a fault somewhere
            // else rather than an ordinary outcome — the aggregate refusing
            // ConfirmOrder is a Rule failure CommandConsumer acks, and the only
            // thing that refuses it is a cancellation, which arrives here on its
            // own event. What is left is the command never being consumed at
            // all: an outbox that stopped, a queue not being drained, a replica
            // that took the acknowledgement during a rollout. Each wants a
            // person.
            When(ConfirmationTimeout.Received)
                .Send(
                    OrderingQueue,
                    ctx => new FlagOrderForReview(ctx.Saga.OrderId, ReviewReasons.NotConfirmed))
                .Finalize(),

            // Shipping can beat this saga to its own acknowledgement: §3.2 gives
            // Shipping OrderConfirmed too, so one publish fans out to two
            // consumers and §9.4 orders nothing between them. Handled rather
            // than ignored because ignoring loses the MarkOrderShipped this
            // branch exists to send, and safely: Shipping learns of the order
            // only from OrderConfirmed, so a despatch arriving at all proves the
            // aggregate committed the confirmation MarkOrderShipped checks. No
            // Unschedule for DespatchTimeout, which is armed on entry to
            // Confirmed and never was here.
            //
            // The review row is raised now because finalising deletes the
            // instance the cancellation's own OrderCancelled would correlate
            // to. On that arm the aggregate will refuse the command — the order
            // is already cancelled (ADR-029), so MarkOrderShippedHandler answers
            // order.not_shippable, a Rule failure CommandConsumer acks (§9.8) —
            // and it is still sent, on §5.4's rule: the aggregate owns the
            // transition and this machine does not predict its answer from a
            // flag.
            When(ShipmentDispatched)
                .Unschedule(ConfirmationTimeout)
                .Send(
                    OrderingQueue,
                    ctx => new MarkOrderShipped(ctx.Saga.OrderId, ctx.Message.TrackingNumber))
                .If(
                    ctx => ctx.Saga.CancellationObserved,
                    cancelled => cancelled
                        .Send(
                            OrderingQueue,
                            ctx => new FlagOrderForReview(
                                ctx.Saga.OrderId,
                                ReviewReasons.CancelledAfterConfirmation)))
                .Finalize(),

            // The early release again, on AwaitingStock's argument: this state's
            // OrderCancelled branch sends a ReleaseStock that ADR-024 has
            // Inventory answer. Recorded because ShipmentDispatched is the
            // forward event here and it finalises; without the flag the
            // cancellation would reach a deleted instance and only the
            // missing-instance fault would notice, whereas the despatch branch
            // above raises the review row, which is the actionable half.
            When(StockReleased)
                .Then(ctx => ctx.Saga.CancellationObserved = true));

        During(
            Confirmed,
            // The same pairing as AwaitingConfirmation's despatch branch, for
            // the same reason: the command is sent whatever else is true, and a
            // cancellation already seen gets its row here rather than losing it
            // to the Finalize. When(OrderCancelled) below is where
            // cancelled_after_confirmation is normally raised; this is the
            // interleaving where that event arrives too late to find an
            // instance.
            When(ShipmentDispatched)
                .Unschedule(DespatchTimeout)
                .Send(
                    OrderingQueue,
                    ctx => new MarkOrderShipped(ctx.Saga.OrderId, ctx.Message.TrackingNumber))
                .If(
                    ctx => ctx.Saga.CancellationObserved,
                    cancelled => cancelled
                        .Send(
                            OrderingQueue,
                            ctx => new FlagOrderForReview(
                                ctx.Saga.OrderId,
                                ReviewReasons.CancelledAfterConfirmation)))
                .Finalize(),

            When(DespatchTimeout.Received)
                // Escalation, not compensation. The saga finalises because it
                // has nothing further to coordinate; a human now owns the order.
                .Send(
                    OrderingQueue,
                    ctx => new FlagOrderForReview(ctx.Saga.OrderId, ReviewReasons.NotDespatched))
                .Finalize(),

            // The card is authorised, and undoing that is a refund §3.2 gives
            // Ordering no command for: Payments voids off OrderCancelled itself,
            // and whether it has yet is unknowable here because §9.4 orders
            // nothing between two consumers, so the runbook checks rather than
            // predicts. What this transition owns is shipping: reaching
            // Confirmed means a despatch may still be moving, which is the
            // difference between this code and Compensating's, and why there is
            // no ReleaseStock — a reservation being picked is not one Inventory
            // can safely be told to drop. The row is where both loose ends are
            // worked.
            //
            // Finalize is what prevents a false not_despatched review, not the
            // Unschedule beside it. ADR-021's delayed-message scheduler cannot
            // cancel, so every Unschedule in this machine is a no-op and the
            // three-day DespatchExpired stays queued; it is harmless because
            // SetCompletedWhenFinalized has deleted the instance, so it
            // correlates to nothing. The Unschedule stays because ADR-021 names
            // Quartz as its supersession, and the calls become live that day.
            When(OrderCancelled)
                .Unschedule(DespatchTimeout)
                // A different code from Compensating's because the row is the
                // only thing an operator gets: ordering.OrderReviews persists
                // (OrderId, Reason, RaisedAt) and the saga has usually finalised
                // before the one-hour alert. The two procedures differ at the
                // first step — from here Shipping may still despatch, and
                // stopping that comes first.
                .Send(
                    OrderingQueue,
                    ctx => new FlagOrderForReview(
                        ctx.Saga.OrderId,
                        ReviewReasons.CancelledAfterConfirmation))
                .Finalize(),

            // A second OrderConfirmed lands here on two ordinary paths: §9.5's
            // unrecorded redelivery, and the rollout — a machine that entered
            // Confirmed when it sent ConfirmOrder publishes OrderConfirmed
            // moments later, and the binding this release declares is durable
            // and queue-scoped, so the first new replica copies those in for as
            // long as §15.5's canary runs both releases. Left unwritten both
            // fault, the deploy case on every order in flight at cutover, and
            // §13.6 pages on the error queue.
            //
            // ADR-032's Entity Framework outbox writes everything the transition
            // into Confirmed emits in the instance's own transaction, so a
            // second arrival is a genuine duplicate or a misroute and never
            // evidence of a loss. The misroute is the whole cost, absorbed in
            // this state only: an Ignore is written for an arrival somebody can
            // name, and the machine keeps its faulting default wherever none
            // has been.
            Ignore(OrderConfirmed),

            // The early release, argued differently from the other three
            // states. This state's When(OrderCancelled) finalises, so a retry
            // discards rather than rescues: one fault, then a clean ack on the
            // redelivery, because a non-initial event correlating to no
            // instance is consumed cleanly. What the line buys is a clean first
            // delivery on every cancellation of a confirmed order. And there is
            // no exit to lose, because this state deliberately sends no
            // ReleaseStock — a reservation being picked cannot safely be
            // dropped — so nothing here rests on ADR-024; the argument is
            // §3.2's fan-out alone.
            //
            // Withholding the command does not keep the reservation: Inventory
            // releases off OrderCancelled regardless, so this arrival is the
            // stock coming back for an order a picker may still be working.
            // cancelled_after_confirmation on When(OrderCancelled) above is the
            // row an operator works it from. Recorded because ShipmentDispatched
            // here finalises, and the row it raises needs the flag.
            When(StockReleased)
                .Then(ctx => ctx.Saga.CancellationObserved = true));

        During(
            Compensating,
            // Compensating has two halves outstanding. It is reached from
            // AwaitingPayment with AuthorisePayment sent and unanswered, so
            // Inventory and Payments are both owed, by different services with
            // §9.4 ordering nothing between them. Either answer may land first,
            // so every exit below asks about the other half, and Finalize is
            // conditional on both being settled: Inventory answering promptly
            // while a PSP is slow is the expected interleaving, and an
            // unconditional Finalize here would delete the instance the
            // authorisation still in flight correlates to — money taken and no
            // one told.
            When(StockReleased)
                .Unschedule(ReleaseTimeout)
                .Then(ctx => ctx.Saga.StockReleaseSettled = true)
                // The reason recorded on entry, not a literal: this transition
                // is reached from a decline and from a timeout alike.
                .Send(
                    OrderingQueue,
                    ctx => new CancelOrder(ctx.Saga.OrderId, ctx.Saga.CancelReason))
                // The order is cancelled either way — that command goes now
                // and does not wait on Payments. What waits is the instance,
                // and only for as long as a verdict can still arrive.
                .If(
                    ctx => !ctx.Saga.PaymentVerdictOutstanding,
                    settled => settled.Finalize()),

            When(ReleaseTimeout.Received)
                // Cancel the order regardless — the customer must not be left
                // waiting on Inventory. The stranded reservation is escalated
                // separately, because it is Inventory's to resolve.
                .Then(ctx => ctx.Saga.StockReleaseSettled = true)
                .Send(
                    OrderingQueue,
                    ctx => new CancelOrder(ctx.Saga.OrderId, ctx.Saga.CancelReason))
                .Send(
                    OrderingQueue,
                    ctx => new FlagOrderForReview(ctx.Saga.OrderId, ReviewReasons.StockNotReleased))
                // Settled means "come to rest", not "succeeded": the stock half
                // is finished either way, and the same payment question decides
                // whether the instance is.
                .If(
                    ctx => !ctx.Saga.PaymentVerdictOutstanding,
                    settled => settled.Finalize()),

            // The money arriving after the cancellation was already the outcome,
            // and the one event this state must not be quiet about: reaching
            // Compensating from AwaitingPayment means AuthorisePayment was sent,
            // so an authorisation can still land. §3.2 gives Ordering no refund
            // command and keys Payments' own void on OrderCancelled — which on
            // the decline and timeout doors is not yet published when this
            // fires, because CancelOrder goes on this state's exits. Nothing
            // here knows whether a refund follows, which is why the row exists
            // and why the runbook checks rather than predicts. Left unwritten
            // it would fault, and the case is owed a row, not a pager.
            //
            // A different code from Confirmed's: an order that reached Confirmed
            // may still be despatched, and this state cannot despatch. Order
            // within the activity matters: the row is raised whether or not
            // this is the last answer owed, and the Finalize is the join rather
            // than part of the escalation.
            When(PaymentAuthorised)
                .Unschedule(PaymentTimeout)
                .Then(ctx => ctx.Saga.PaymentVerdictOutstanding = false)
                .Send(
                    OrderingQueue,
                    ctx => new FlagOrderForReview(
                        ctx.Saga.OrderId,
                        ReviewReasons.PaymentAuthorisedDuringCompensation))
                .If(
                    ctx => ctx.Saga.StockReleaseSettled,
                    settled => settled.Finalize()),

            // The confirmation that arrives after compensation has begun, and
            // the one thing that can prove AwaitingConfirmation's cancellation
            // ran on a false premise: that state cancels assuming the aggregate
            // had not confirmed, which is unknowable when the branch runs
            // because OrderConfirmed and OrderCancelled are two of Ordering's
            // own outbox rows and §9.4 orders nothing between them. If it
            // arrives, the order was confirmed, Shipping was told, a despatch
            // may be moving and a ReleaseStock is already in flight — exactly
            // cancelled_after_confirmation's case, so it raises Confirmed's
            // code.
            //
            // Not Ignore, which would restore the silence one state over; not
            // a fault, because the arrival is legitimate and has a row. No
            // Finalize, conditional or otherwise: a confirmation is not one of
            // the two answers this state waits on, so it raises its row and
            // leaves the instance as it found it. It does not recall the
            // release because §3.2 gives Inventory no way to be told "keep the
            // reservation after all"; the row is the mechanism.
            When(OrderConfirmed)
                .Send(
                    OrderingQueue,
                    ctx => new FlagOrderForReview(
                        ctx.Saga.OrderId,
                        ReviewReasons.CancelledAfterConfirmation)),

            // Written rather than left to fault, so a reader can tell a
            // decision from an omission. Reaching Compensating means a
            // cancellation is already the outcome, so a customer cancellation
            // arriving now adds nothing: the exits cancel the order regardless,
            // and Order.Cancel is idempotent, so the second CancelOrder is a
            // no-op.
            Ignore(OrderCancelled),

            // The two Inventory answers to a reservation this saga no longer
            // wants, both reachable by cancelling in AwaitingStock and both
            // races by design: Compensating's exits own the cancellation, so
            // neither has work left here. In flight is not effective, though —
            // §9.4 orders nothing, so Inventory may handle the release before
            // the reserve it was meant to undo. ADR-024 closes that on the
            // receiver: Inventory remembers a release for an order whose
            // ReserveStock has not arrived and refuses the reserve that follows,
            // answering with StockReleased (not StockReservationFailed, which
            // reports unavailable products).
            //
            // It could not be closed here. A second ReleaseStock on a late
            // StockReserved needs this state to still hold an instance, and
            // under ADR-024's other half the no-op release has already
            // published and the exit above has already finalised. Only the
            // receiver still has both facts, which is why the guarantee is
            // Inventory's, and why both events are absorbed on a stated
            // contract rather than a hope about ordering.
            Ignore(StockReserved),
            Ignore(StockReservationFailed),

            // Reachable because the OrderCancelled door arrives with the
            // authorisation still outstanding, so its verdict can be a decline
            // as well as an authorisation. Not escalated: no money moved, which
            // is the outcome compensation was heading for, so there is nothing
            // for a human to do. Not ignored either: a decline is an answer
            // that discharges the obligation the cancellation carried in, and
            // while that stands the stock exits above will not finalise, so
            // ignoring it would hold the instance open for a verdict that had
            // already arrived.
            When(PaymentDeclined)
                .Unschedule(PaymentTimeout)
                .Then(ctx => ctx.Saga.PaymentVerdictOutstanding = false)
                .If(
                    ctx => ctx.Saga.StockReleaseSettled,
                    settled => settled.Finalize()),

            // The bound on how long a cancelled order waits for a verdict, and
            // the only exit here that ends the wait without an answer. It is
            // armed a second time by AwaitingPayment's timeout branch and still
            // running from the original AuthorisePayment on the cancellation
            // branch. No review row, because the ordinary case is that no
            // verdict ever comes: §3.2 has Payments consuming OrderCancelled,
            // so an authorisation abandoned on a cancelled order is what should
            // happen, and a row would page someone for every one the PSP
            // correctly dropped. The escalation belongs where money moved,
            // which is the PaymentAuthorised branch above.
            //
            // What it leaves open: an authorisation landing after this fires
            // finds no instance. OnMissingInstance faults it onto §13.6's
            // pager, but the review row is beyond a machine that has stopped
            // waiting, and closing that means persisting the obligation
            // outside the saga, which is a chapter decision of its own.
            When(PaymentTimeout.Received)
                .Then(ctx => ctx.Saga.PaymentVerdictOutstanding = false)
                .If(
                    ctx => ctx.Saga.StockReleaseSettled,
                    settled => settled.Finalize()));

        SetCompletedWhenFinalized();
    }

    /// <summary>
    /// What to do with an <c>OrderCancelled</c> that correlates to no
    /// instance: returns for the two arrivals this service can account for
    /// and throws for every other, which puts them in front of a person.
    /// </summary>
    /// <remarks>
    /// It throws what <c>Fault()</c> would have thrown. The built-in cannot be
    /// reached from here — <c>OnMissingInstance</c> takes one configurator and
    /// the decision needs the message — so the branch raises the same
    /// <see cref="SagaException"/>, and the error-queue entry reads identically
    /// to <c>PaymentAuthorised</c>'s, so the runbook needs one procedure rather
    /// than two.
    /// </remarks>
    private static Task NoInstanceForCancellation(ConsumeContext<OrderCancelled> context)
    {
        // Allow-list. Absent is the permanent V1 tolerance argued at the
        // registration — a retained payload can arrive indefinitely — and
        // workflow is this service's own echo.
        if (context.Message.Origin is null or CancelOrigins.Workflow)
            return Task.CompletedTask;

        throw new SagaException(
            $"An OrderCancelled with origin '{context.Message.Origin}' correlated to no saga instance.",
            typeof(OrderFulfilmentState),
            typeof(OrderCancelled),
            context.Message.OrderId);
    }
}
