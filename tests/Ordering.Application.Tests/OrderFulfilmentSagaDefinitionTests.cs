using MassTransit;
using Ordering.Infrastructure.Messaging;
using Shouldly;
using Xunit;

namespace Ordering.Application.Tests;

/// <summary>§9.6's saga read without running it: the states, events and schedules the machine declares.</summary>
[Collection(nameof(OrderFulfilmentSagaCollection))]
public class OrderFulfilmentSagaDefinitionTests
{
    [Fact]
    public void The_machine_declares_the_states_the_chapter_draws_and_no_others()
    {
        // Cancelled and Shipped are outcomes, not states (§9.6); Initial and Final are MassTransit's.
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
        // A partition, so a new event fails until classified and a reachable one fails without its branch.
        OrderFulfilmentSaga saga = new();

        string[] reachableHere =
        [
            nameof(saga.StockReleased),
            nameof(saga.PaymentAuthorised),
            nameof(saga.PaymentDeclined),
            nameof(saga.OrderCancelled),
            nameof(saga.StockReserved),
            nameof(saga.StockReservationFailed),

            // AwaitingConfirmation is the door entered with an OrderConfirmed still outstanding.
            nameof(saga.OrderConfirmed),
            $"{nameof(saga.ReleaseTimeout)}.Received",

            // The exit that ends the payment wait a cancellation from AwaitingPayment leaves armed.
            $"{nameof(saga.PaymentTimeout)}.Received"
        ];

        // OrderPlaced only creates an instance; the others belong to other states.
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
                "Compensating or not; an event in neither list is one nobody decided about (§9.6).");

        // .AnyReceived is MassTransit's own, accepted in every state, so it says nothing about a branch.
        saga
            .NextEvents(saga.Compensating)
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
        // Not a partition: an event with no branch and no entry passes here, which the test below closes.
        OrderFulfilmentSaga saga = new();

        // StockReleased can beat the saga's own OrderCancelled, since Inventory releases on the event (ADR-029).
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

        // A despatch can beat the acknowledgement, since §9.4 orders nothing between OrderConfirmed's consumers.
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

        // A second OrderConfirmed here is a duplicate, and StockReleased is Inventory acting on the event alone.
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
        // Both sides are read from the machine, so there is no list to forget.
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

    /// <summary>Every declared event, a <see cref="Schedule{TInstance, TMessage}"/> as its <c>.Received</c>.</summary>
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
        saga
            .NextEvents(state)
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
        // Structural, because a wait whose schedule is deleted has no behavioural test left to go red.
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

        // Equality, so a wait state without a schedule fails and so does a schedule left behind.
        schedules.Length.ShouldBe(saga.States.Count(s => s.Name is not ("Initial" or "Final")));
    }
}
