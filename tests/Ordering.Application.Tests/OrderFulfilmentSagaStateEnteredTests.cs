using Common.Contracts.Inventory.V1;
using Common.Contracts.Payments.V1;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Infrastructure.Messaging;
using Shouldly;
using Xunit;
using static Ordering.Application.Tests.OrderFulfilmentSagaHarness;

namespace Ordering.Application.Tests;

/// <summary>§9.6's saga stamping each state it enters, so the saga read can say how long an order has sat in one.</summary>
[Collection(nameof(OrderFulfilmentSagaCollection))]
public class OrderFulfilmentSagaStateEnteredTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_transition_stamps_the_time_it_entered_the_new_state()
    {
        SteppedClock clock = new(Start);
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync(clock: clock);
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            (await Sent<ReserveStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            // A later clock, so a stamp left from the first state would read as the earlier value.
            clock.Now = Start.AddMinutes(7);
            await Publish(harness, SagaContracts.StockReserved(orderId));
            (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            (await EnteredAt(harness, orderId, m => m.AwaitingPayment)).ShouldBe(Start.AddMinutes(7));
        }
    }

    [Fact]
    public async Task The_first_entry_is_stamped_too()
    {
        // Initially has no state to leave, and its entry is the first stamp the read can ask for.
        SteppedClock clock = new(Start);
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync(clock: clock);
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));

            (await EnteredAt(harness, orderId, m => m.AwaitingStock)).ShouldBe(Start);
        }
    }

    [Fact]
    public async Task An_event_that_stays_in_the_state_leaves_the_stamp_alone()
    {
        // The early release is recorded without a transition (§9.6), so the state was not entered again.
        SteppedClock clock = new(Start);
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync(clock: clock);
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            clock.Now = Start.AddMinutes(3);
            await Publish(harness, SagaContracts.StockReleased(orderId));

            (await EnteredAt(harness, orderId, m => m.AwaitingStock)).ShouldBe(Start);
        }
    }

    private static async Task<DateTimeOffset?> EnteredAt(
        ITestHarness harness,
        Guid orderId,
        Func<OrderFulfilmentSaga, State> state)
    {
        ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
            harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

        // Waits for the instance to rest in the state, since a send is observed before the row is saved.
        (await saga.Exists(orderId, state)).ShouldNotBeNull();

        return saga.Sagas.Select(x => x.CorrelationId == orderId).Single().Saga.StateEnteredAt;
    }

    /// <summary>A clock the test moves, so each stamp is a value it chose.</summary>
    private sealed class SteppedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
