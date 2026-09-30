using Common.Contracts.Inventory.V1;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Ordering.Application.Tests.OrderFulfilmentSagaHarness;

namespace Ordering.Application.Tests;

/// <summary>The barrier in <see cref="OrderFulfilmentSagaHarness.Publish"/>, which the saga tests lean on.</summary>
[Collection(nameof(OrderFulfilmentSagaCollection))]
public class OrderFulfilmentSagaBarrierTests
{
    [Fact]
    public async Task A_publish_returns_only_after_the_saga_has_consumed_that_message()
    {
        // Read as of now: had Publish returned early, the command Initially sends would not be recorded yet.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));

            harness.Sent
                .Select<ReserveStock>(Spent())
                .Count(m => m.Context.Message.OrderId == orderId)
                .ShouldBe(1);

            // Two facts with two ids, which a type-level wait could not tell apart.
            StockReserved first = SagaContracts.StockReserved(orderId);
            StockReserved second = SagaContracts.StockReserved(orderId);
            await Publish(harness, first);
            await Publish(harness, second);

            harness.Consumed
                .Select<StockReserved>(Spent())
                .Count(m => m.Context.Message.OrderId == orderId)
                .ShouldBe(2);

            // AwaitingPayment declares no StockReserved branch, so exactly one fault proves two deliveries.
            ConsumeFaults<StockReserved>(harness)
                .Count(e => e != null)
                .ShouldBe(1);
        }
    }

    /// <summary>A consumer that returns only when the test lets it, so no timing decides the barrier.</summary>
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

    private sealed record GateProbe(Guid Id);

    [Fact]
    public async Task A_publish_does_not_return_while_its_own_message_is_still_being_consumed()
    {
        // Publish must not return while its message is unconsumed, nor on another message of the same type.
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

            // The release goes in a finally, or a failing assertion leaves the consumer blocked and the run hung.
            Task publish = Publish(harness, new GateProbe(Guid.CreateVersion7()));
            try
            {
                // Bounded so a probe that never routed fails the run rather than hanging it.
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

            // A type-level wait is already satisfied by the first probe; an id-level wait is not.
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
