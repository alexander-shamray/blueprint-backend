using System.Collections.Concurrent;
using Common.Contracts.Inventory.V1;
using Inventory.Infrastructure.Messaging;
using Inventory.TestSupport;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Inventory.Api.Tests;

/// <summary>The exclusion faults a malformed message before <see cref="RetryPolicy.Standard"/>'s ladder.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class RetryExclusionTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Bus-wide, since MassTransit constructs a fresh <c>CommandConsumer</c> per delivery.</summary>
    private sealed class FaultCountingObserver : IConsumeObserver
    {
        private readonly ConcurrentDictionary<Guid, int> _faults = new();
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _entered = new();

        public Task PreConsume<T>(ConsumeContext<T> context)
            where T : class
        {
            Entry(context.MessageId ?? Guid.Empty).TrySetResult();
            return Task.CompletedTask;
        }

        public Task PostConsume<T>(ConsumeContext<T> context)
            where T : class => Task.CompletedTask;

        public Task ConsumeFault<T>(ConsumeContext<T> context, Exception exception)
            where T : class
        {
            _faults.AddOrUpdate(context.MessageId ?? Guid.Empty, 1, static (_, count) => count + 1);
            return Task.CompletedTask;
        }

        public int FaultsFor(Guid messageId) => _faults.GetValueOrDefault(messageId);

        /// <summary>Completes once a consumer is entered for <paramref name="messageId"/>.</summary>
        public Task Entered(Guid messageId) => Entry(messageId).Task;

        private TaskCompletionSource Entry(Guid messageId) =>
            _entered.GetOrAdd(
                messageId,
                static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    [Fact]
    public async Task A_malformed_reserve_faults_exactly_once_inside_the_first_retry_interval()
    {
        IBus bus = fixture.Factory.Services.GetRequiredService<IBus>();
        var observer = new FaultCountingObserver();
        using ConnectHandle handle = bus.ConnectConsumeObserver(observer);

        var messageId = Guid.CreateVersion7();
        Task entered = observer.Entered(messageId);
        ISendEndpoint endpoint = await bus.GetSendEndpoint(
            new Uri($"queue:{DependencyInjection.CommandsQueue}"));
        await endpoint.Send(
            new ReserveStock(Guid.CreateVersion7(), [new StockLine(Guid.Empty, 1)]),
            c => c.MessageId = messageId,
            TestContext.Current.CancellationToken);

        // The window below opens at the attempt, not the send, which a loaded runner can leave queued.
        await Task.WhenAny(
            entered,
            Task.Delay(ReservationTestSupport.DeliveryBudget, TestContext.Current.CancellationToken));
        entered.IsCompletedSuccessfully.ShouldBeTrue(
            $"no consumer was entered for {messageId} within {ReservationTestSupport.DeliveryBudget}, " +
            "so the interval below would be timing a delivery nothing has looked at");

        // Past the ladder's first wait by a jitter margin; a message still on the ladder has not faulted by then.
        await Task.Delay(
            RetryPolicy.MinInterval + TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        observer.FaultsFor(messageId).ShouldBe(
            1,
            "the fault would not have landed yet if Messaging/DependencyInjection.cs's " +
            "r.Ignore<ContractMappingException>() were removed — RetryPolicy.Standard's ladder " +
            "would still be waiting out RetryPolicy.MinInterval before its first retry");
    }
}
