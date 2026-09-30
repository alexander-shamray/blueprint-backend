using Common.Contracts.Inventory.V1;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Payments.V1;
using Common.Contracts.Shipping.V1;
using MassTransit;
using static Ordering.Infrastructure.Messaging.Endpoints;

namespace Ordering.Infrastructure.Messaging;

/// <summary>§9.6's order fulfilment saga; the diagram there is this machine's specification.</summary>
/// <remarks>Commands are sent, never published, so exactly one owning service executes them (§9.6).</remarks>
public sealed class OrderFulfilmentSaga : MassTransitStateMachine<OrderFulfilmentState>
{
    /// <summary>How long §9.6 waits for Inventory to answer <c>ReserveStock</c>.</summary>
    public static readonly TimeSpan StockTimeoutDelay = TimeSpan.FromMinutes(5);

    /// <summary>How long §9.6 waits for Payments' verdict; longer than stock, because a PSP retry is normal.</summary>
    public static readonly TimeSpan PaymentTimeoutDelay = TimeSpan.FromMinutes(15);

    /// <summary>How long §9.6 waits for this service's own <c>ConfirmOrder</c> to be acknowledged.</summary>
    public static readonly TimeSpan ConfirmationTimeoutDelay = TimeSpan.FromMinutes(10);

    /// <summary>How long §9.6 waits for a <c>ReleaseStock</c> it sent while compensating.</summary>
    public static readonly TimeSpan ReleaseTimeoutDelay = TimeSpan.FromMinutes(10);

    /// <summary>How long §9.6 waits for despatch once confirmed: days, because the far end is a warehouse.</summary>
    public static readonly TimeSpan DespatchTimeoutDelay = TimeSpan.FromDays(3);

    // §9.6's states exactly; Cancelled and Shipped are outcomes, deleted by SetCompletedWhenFinalized().
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

    // Ordering's own event (§3.2); binding it on a live queue ships consumer-first (ADR-026, §15.5).
    public Event<OrderCancelled> OrderCancelled { get; private set; } = null!;

    // The acknowledgement AwaitingConfirmation waits for, at no contract cost (§9.6). An old replica handed an
    // instance in AwaitingConfirmation throws UnknownStateException, which draining or a clean cutover closes (§15.5).
    public Event<OrderConfirmed> OrderConfirmed { get; private set; } = null!;

    // One schedule per wait (§9.6).
    public Schedule<OrderFulfilmentState, StockReservationExpired> StockTimeout { get; private set; } = null!;
    public Schedule<OrderFulfilmentState, PaymentAuthorisationExpired> PaymentTimeout { get; private set; } = null!;
    public Schedule<OrderFulfilmentState, ConfirmationExpired> ConfirmationTimeout { get; private set; } = null!;
    public Schedule<OrderFulfilmentState, DespatchExpired> DespatchTimeout { get; private set; } = null!;
    public Schedule<OrderFulfilmentState, StockReleaseExpired> ReleaseTimeout { get; private set; } = null!;

    public OrderFulfilmentSaga()
    {
        InstanceState(x => x.CurrentState);

        // No catch-all: an event with no transition in its state faults, and every legitimate arrival is written
        // out with an Ignore or a recording branch (§9.6).

        // Correlated on the order, which is also what §9.3's mapper sets CorrelationId to.
        Event(() => OrderPlaced, x => x.CorrelateById(m => m.Message.OrderId));
        Event(() => StockReserved, x => x.CorrelateById(m => m.Message.OrderId));
        Event(() => StockReservationFailed, x => x.CorrelateById(m => m.Message.OrderId));

        // The one event whose missing instance always faults: Payments produces it, so it is never an echo (§9.6).
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

        // Discarded with no instance only for the arrivals NoInstanceForCancellation allows, faulted otherwise (§9.6).
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

        Schedule(
            () => PaymentTimeout,
            x => x.PaymentTimeoutTokenId,
            s =>
            {
                s.Delay = PaymentTimeoutDelay;
                s.Received = e => e.CorrelateById(m => m.Message.OrderId);
            });

        // The one wait whose far end is this service. §9.4's dispatcher backoff, not §9.8's retry ladder, decides
        // it, and it is deliberately not long enough to outlast that backoff: an outbox stuck for §13.6's
        // abandoned-row alert escalates as not_confirmed rather than being waited through. It escalates rather
        // than compensating, because §3.2 gives Ordering no refund command.
        Schedule(
            () => ConfirmationTimeout,
            x => x.ConfirmationTimeoutTokenId,
            s =>
            {
                s.Delay = ConfirmationTimeoutDelay;
                s.Received = e => e.CorrelateById(m => m.Message.OrderId);
            });

        // No automatic compensation remains, so the timeout escalates to a human (§9.6).
        Schedule(
            () => DespatchTimeout,
            x => x.DespatchTimeoutTokenId,
            s =>
            {
                s.Delay = DespatchTimeoutDelay;
                s.Received = e => e.CorrelateById(m => m.Message.OrderId);
            });

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
            // Withheld once a cancellation is observed (§9.6); StockTimeout stays armed to bound the wait for it.
            When(StockReserved)
                .If(
                    ctx => !ctx.Saga.CancellationObserved,
                    proceed => proceed
                        .Unschedule(StockTimeout)
                        // Recorded where the obligation is incurred (ADR-025).
                        .Then(ctx => ctx.Saga.PaymentVerdictOutstanding = true)
                        // No subject travels (ADR-028): Payments derives the payer from its own record of the order.
                        .Send(
                            PaymentsQueue,
                            ctx => new AuthorisePayment(
                                ctx.Saga.OrderId,
                                ctx.Saga.Total,
                                ctx.Saga.Currency))
                        .Schedule(PaymentTimeout, ctx => new PaymentAuthorisationExpired(ctx.Saga.OrderId))
                        .TransitionTo(AwaitingPayment)),

            When(StockReservationFailed)
                .Unschedule(StockTimeout)
                .Send(
                    OrderingQueue,
                    ctx => new CancelOrder(ctx.Saga.OrderId, CancelReasons.OutOfStock))
                .Finalize(),

            When(StockTimeout.Received)
                .Send(
                    OrderingQueue,
                    ctx => new CancelOrder(ctx.Saga.OrderId, CancelReasons.StockTimeout))
                .Finalize(),

            // Compensates rather than finalising, so the release keeps its timeout (§9.6).
            When(OrderCancelled)
                .Unschedule(StockTimeout)
                // The event's reason, not a literal (§9.6).
                .Then(ctx => ctx.Saga.CancelReason = ctx.Message.Reason)
                .Send(InventoryQueue, ctx => new ReleaseStock(ctx.Saga.OrderId))
                .Schedule(ReleaseTimeout, ctx => new StockReleaseExpired(ctx.Saga.OrderId))
                .TransitionTo(Compensating),

            // Inventory's release beating this saga's own OrderCancelled; recorded so StockReserved withholds (§9.6).
            When(StockReleased)
                .Then(ctx => ctx.Saga.CancellationObserved = true));

        During(
            AwaitingPayment,
            // Sends ConfirmOrder; Confirmed is entered on the aggregate's own OrderConfirmed (§9.6).
            When(PaymentAuthorised)
                .Then(ctx => ctx.Saga.PaymentVerdictOutstanding = false)
                // After an observed cancellation it escalates and stays (§9.6), and leaves PaymentTimeout armed to
                // bound an instance whose OrderCancelled never arrives.
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
                // Recorded on entry: by the time Compensating's shared exits run, the triggering event is gone.
                .Then(ctx => ctx.Saga.CancelReason = CancelReasons.PaymentDeclined)
                .Send(InventoryQueue, ctx => new ReleaseStock(ctx.Saga.OrderId))
                .Schedule(ReleaseTimeout, ctx => new StockReleaseExpired(ctx.Saga.OrderId))
                .TransitionTo(Compensating),

            When(PaymentTimeout.Received)
                // A decline's compensation, not its reason: a silent PSP is not a declined customer (§13.3).
                .Then(ctx => ctx.Saga.CancelReason = CancelReasons.PaymentTimeout)
                .Send(InventoryQueue, ctx => new ReleaseStock(ctx.Saga.OrderId))
                .Schedule(ReleaseTimeout, ctx => new StockReleaseExpired(ctx.Saga.OrderId))
                // The obligation stays set and the wait is armed once more, bounding the hold (§9.6, ADR-025).
                .Schedule(PaymentTimeout, ctx => new PaymentAuthorisationExpired(ctx.Saga.OrderId))
                .TransitionTo(Compensating),

            // Leaves PaymentTimeout armed: Payments still owes a verdict, and Compensating receives the expiry (§9.6).
            When(OrderCancelled)
                .Then(ctx => ctx.Saga.CancelReason = ctx.Message.Reason)
                .Send(InventoryQueue, ctx => new ReleaseStock(ctx.Saga.OrderId))
                .Schedule(ReleaseTimeout, ctx => new StockReleaseExpired(ctx.Saga.OrderId))
                .TransitionTo(Compensating),

            // The early release again (§9.6); recorded so PaymentAuthorised escalates rather than confirms.
            When(StockReleased)
                .Then(ctx => ctx.Saga.CancellationObserved = true));

        // ConfirmOrder is in flight and nothing downstream knows yet (§9.6).
        During(
            AwaitingConfirmation,
            // The first moment a despatch can be expected, so DespatchTimeout is armed here. An observed
            // cancellation still transitions, raising its row (§9.6).
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

            // No escalation: there is no despatch to stop, and Payments voids off OrderCancelled itself (§9.6).
            When(OrderCancelled)
                .Unschedule(ConfirmationTimeout)
                .Then(ctx => ctx.Saga.CancelReason = ctx.Message.Reason)
                .Send(InventoryQueue, ctx => new ReleaseStock(ctx.Saga.OrderId))
                .Schedule(ReleaseTimeout, ctx => new StockReleaseExpired(ctx.Saga.OrderId))
                .TransitionTo(Compensating),

            // Out of moves with the card authorised: ConfirmOrder was never consumed, which wants a person (§9.6).
            When(ConfirmationTimeout.Received)
                .Send(
                    OrderingQueue,
                    ctx => new FlagOrderForReview(ctx.Saga.OrderId, ReviewReasons.NotConfirmed))
                .Finalize(),

            // Shipping can beat this saga to the acknowledgement (§3.2), and a despatch proves the confirmation
            // committed. Sent even after an observed cancellation: the aggregate owns the answer (§5.4).
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

            // The early release again (§9.6); recorded because ShipmentDispatched finalises here.
            When(StockReleased)
                .Then(ctx => ctx.Saga.CancellationObserved = true));

        During(
            Confirmed,
            // As AwaitingConfirmation's despatch branch: an observed cancellation gets its row before the Finalize.
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
                // Escalation, not compensation: a human now owns the order.
                .Send(
                    OrderingQueue,
                    ctx => new FlagOrderForReview(ctx.Saga.OrderId, ReviewReasons.NotDespatched))
                .Finalize(),

            // Escalates without ReleaseStock, since a despatch may be moving (§9.6, ADR-029). Finalize, not the
            // no-op Unschedule, keeps the queued DespatchExpired harmless (ADR-021).
            When(OrderCancelled)
                .Unschedule(DespatchTimeout)
                .Send(
                    OrderingQueue,
                    ctx => new FlagOrderForReview(
                        ctx.Saga.OrderId,
                        ReviewReasons.CancelledAfterConfirmation))
                .Finalize(),

            // A second OrderConfirmed is §9.5's unrecorded redelivery or a rollout's copy, absorbed here only (§9.6).
            Ignore(OrderConfirmed),

            // The early release; this state sends no release, so nothing here rests on ADR-024 (§9.6). Recorded
            // because ShipmentDispatched finalises here and its row needs the flag.
            When(StockReleased)
                .Then(ctx => ctx.Saga.CancellationObserved = true));

        During(
            Compensating,
            // Two halves are owed, and every exit asks about the other before finalising (ADR-025).
            When(StockReleased)
                .Unschedule(ReleaseTimeout)
                .Then(ctx => ctx.Saga.StockReleaseSettled = true)
                // The reason recorded on entry, not a literal: this transition
                // is reached from a decline and from a timeout alike.
                .Send(
                    OrderingQueue,
                    ctx => new CancelOrder(ctx.Saga.OrderId, ctx.Saga.CancelReason))
                // The order is cancelled either way — that command goes now
                // and does not wait on Payments. What waits is the instance.
                .If(
                    ctx => !ctx.Saga.PaymentVerdictOutstanding,
                    settled => settled.Finalize()),

            When(ReleaseTimeout.Received)
                // Cancelled regardless; the stranded reservation is Inventory's to resolve, so it is escalated.
                .Then(ctx => ctx.Saga.StockReleaseSettled = true)
                .Send(
                    OrderingQueue,
                    ctx => new CancelOrder(ctx.Saga.OrderId, ctx.Saga.CancelReason))
                .Send(
                    OrderingQueue,
                    ctx => new FlagOrderForReview(ctx.Saga.OrderId, ReviewReasons.StockNotReleased))
                // Settled means come to rest, not succeeded (§9.6).
                .If(
                    ctx => !ctx.Saga.PaymentVerdictOutstanding,
                    settled => settled.Finalize()),

            // Money after the cancellation: owed a row, not a pager, since §3.2 gives Ordering no refund command.
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

            // Proves AwaitingConfirmation cancelled on a false premise: Confirmed's code, the instance kept (§9.6).
            When(OrderConfirmed)
                .Send(
                    OrderingQueue,
                    ctx => new FlagOrderForReview(
                        ctx.Saga.OrderId,
                        ReviewReasons.CancelledAfterConfirmation)),

            // A cancellation is already the outcome, and Order.Cancel is idempotent.
            Ignore(OrderCancelled),

            // Absorbed on ADR-024's contract: Inventory refuses a reserve that follows the release it answered.
            Ignore(StockReserved),
            Ignore(StockReservationFailed),

            // A decline discharges the obligation, so it is recorded rather than ignored; no money moved, so no row.
            When(PaymentDeclined)
                .Unschedule(PaymentTimeout)
                .Then(ctx => ctx.Saga.PaymentVerdictOutstanding = false)
                .If(
                    ctx => ctx.Saga.StockReleaseSettled,
                    settled => settled.Finalize()),

            // The one exit that ends the wait without an answer, and raises no row: an authorisation abandoned on a
            // cancelled order is the ordinary case, and one landing after finalisation faults (§9.6).
            When(PaymentTimeout.Received)
                .Then(ctx => ctx.Saga.PaymentVerdictOutstanding = false)
                .If(
                    ctx => ctx.Saga.StockReleaseSettled,
                    settled => settled.Finalize()));

        SetCompletedWhenFinalized();
    }

    /// <summary>Returns for the two arrivals this service can account for and throws for every other.</summary>
    /// <remarks>Throws what <c>Fault()</c> would, so the error queue reads as for PaymentAuthorised (§9.6).</remarks>
    private static Task NoInstanceForCancellation(ConsumeContext<OrderCancelled> context)
    {
        // Allow-list: absent is V1's permanent tolerance (§9.2), and workflow is this service's own echo.
        if (context.Message.Origin is null or CancelOrigins.Workflow)
            return Task.CompletedTask;

        throw new SagaException(
            $"An OrderCancelled with origin '{context.Message.Origin}' correlated to no saga instance.",
            typeof(OrderFulfilmentState),
            typeof(OrderCancelled),
            context.Message.OrderId);
    }
}
