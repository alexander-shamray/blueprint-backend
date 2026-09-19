using System.Collections.Concurrent;
using Common.Contracts.Inventory.V1;
using Inventory.Infrastructure.Messaging;
using Inventory.TestSupport;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Inventory.Api.Tests;

/// <summary>
/// The exclusion makes a malformed message fault at once rather than after
/// <see cref="RetryPolicy.Standard"/>'s whole ladder runs its course, so a
/// fault observed inside <see cref="RetryPolicy.MinInterval"/> is the
/// exclusion acting rather than the ladder's own first wait.
/// </summary>
[Collection(nameof(IntegrationCollection))]
public sealed class RetryExclusionTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Bus-wide, so it sees every consume attempt on <c>inventory-commands</c>
    /// rather than one bound to a single consumer instance — MassTransit
    /// constructs a fresh <c>CommandConsumer</c> per delivery.
    /// </summary>
    private sealed class FaultCountingObserver : IConsumeObserver
    {
        private readonly ConcurrentDictionary<Guid, int> _faults = new();

        public Task PreConsume<T>(ConsumeContext<T> context)
            where T : class => Task.CompletedTask;

        public Task PostConsume<T>(ConsumeContext<T> context)
            where T : class => Task.CompletedTask;

        public Task ConsumeFault<T>(ConsumeContext<T> context, Exception exception)
            where T : class
        {
            _faults.AddOrUpdate(context.MessageId ?? Guid.Empty, 1, static (_, count) => count + 1);
            return Task.CompletedTask;
        }

        public int FaultsFor(Guid messageId) => _faults.GetValueOrDefault(messageId);
    }

    [Fact]
    public async Task A_malformed_reserve_faults_exactly_once_inside_the_first_retry_interval()
    {
        IBus bus = fixture.Factory.Services.GetRequiredService<IBus>();
        var observer = new FaultCountingObserver();
        using ConnectHandle handle = bus.ConnectConsumeObserver(observer);

        var messageId = Guid.CreateVersion7();
        ISendEndpoint endpoint = await bus.GetSendEndpoint(
            new Uri($"queue:{DependencyInjection.CommandsQueue}"));
        await endpoint.Send(
            new ReserveStock(Guid.CreateVersion7(), [new StockLine(Guid.Empty, 1)]),
            c => c.MessageId = messageId,
            TestContext.Current.CancellationToken);

        // The sentinel behind it on the same queue: its own row is the signal
        // that the broker round trip and the handler pipeline both ran, which
        // is what makes the delay below a wait for the retry ladder rather
        // than for delivery itself.
        var sentinelProduct = Guid.CreateVersion7();
        await ReservationTestSupport.SeedStock(fixture, sentinelProduct, 1);
        var sentinelOrder = Guid.CreateVersion7();
        await ReservationTestSupport.SendAsync(
            fixture, new ReserveStock(sentinelOrder, [new StockLine(sentinelProduct, 1)]));
        await ReservationTestSupport.EventuallyStatus(fixture, sentinelOrder, "Reserved");

        // RetryPolicy.MinInterval is the ladder's first wait; a message still
        // on it has not yet faulted, so this window closes well before a
        // retried attempt could. The margin absorbs scheduling jitter without
        // reaching into the ladder's second, longer wait.
        await Task.Delay(
            RetryPolicy.MinInterval + TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        observer.FaultsFor(messageId).ShouldBe(
            1,
            "the fault would not have landed yet if Messaging/DependencyInjection.cs's " +
            "r.Ignore<ContractMappingException>() were removed — RetryPolicy.Standard's ladder " +
            "would still be waiting out RetryPolicy.MinInterval before its first retry");
    }
}
