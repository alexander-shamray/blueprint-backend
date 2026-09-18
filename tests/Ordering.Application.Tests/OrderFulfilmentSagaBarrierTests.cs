using Common.Contracts.Inventory.V1;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Ordering.Application.Tests.OrderFulfilmentSagaHarness;

namespace Ordering.Application.Tests;

/// <summary>
/// The barrier in <see cref="OrderFulfilmentSagaHarness.Publish"/>, which
/// every other class in the suite leans on.
/// </summary>
[Collection(nameof(OrderFulfilmentSagaCollection))]
public class OrderFulfilmentSagaBarrierTests
{
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
    /// <see cref="OrderFulfilmentSagaHarness.Publish"/> wait?" has an answer
    /// that does not depend on timing.
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
