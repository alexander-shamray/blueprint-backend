using MassTransit;
using Ordering.Infrastructure.Messaging;
using Shouldly;
using Xunit;

namespace Ordering.Application.Tests;

/// <summary>
/// §9.6's saga read without running it: the states, events and schedules
/// the machine declares.
/// </summary>
[Collection(nameof(OrderFulfilmentSagaCollection))]
public class OrderFulfilmentSagaDefinitionTests
{
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
}
