using Common.Contracts;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Infrastructure.Messaging;
using Shouldly;
using Xunit;

namespace Ordering.Application.Tests;

/// <summary>What §12.5's suite shares: §9.6's saga over MassTransit's in-memory harness.</summary>
internal static class OrderFulfilmentSagaHarness
{
    /// <summary>§12.5's two bounds, stated rather than inherited, the ceiling clear of the other.</summary>
    internal static readonly TimeSpan InactivityTimeout = TimeSpan.FromSeconds(10);

    internal static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(60);

    internal static readonly Guid Customer = Guid.Parse("2a1c9e64-77b1-4b0e-9a3e-6d9c1c2f5a11");

    /// <summary>The registration every test shares, with production's two scheduler lines (ADR-021).</summary>
    /// <remarks><c>Initially</c> arms <c>StockTimeout</c>, so without one <c>OrderPlaced</c> faults (§9.6).</remarks>
    internal static ServiceProvider BuildProvider() =>
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

    internal static async Task<(ServiceProvider Provider, ITestHarness Harness)> StartHarnessAsync()
    {
        ServiceProvider provider = BuildProvider();
        ITestHarness harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        return (provider, harness);
    }

    /// <summary>Publishes and returns once that message id is consumed, faulted or not, ordering publishes.</summary>
    internal static async Task Publish<T>(ITestHarness harness, T message)
        where T : class
    {
        Guid? messageId = null;
        await harness.Bus.Publish(
            message,
            context =>
            {
                // §9.1: body, row, header and inbox key are one GUID.
                if (message is IIntegrationEvent integrationEvent)
                {
                    context.MessageId = integrationEvent.MessageId;
                    context.CorrelationId = integrationEvent.CorrelationId;
                }

                // The send context, because a scheduled expiry has no envelope to read an id from.
                messageId = context.MessageId;
            },
            TestContext.Current.CancellationToken);

        // Unset, null == null would match the first consume of T and fence nothing.
        messageId.ShouldNotBeNull();

        (await ConsumedWithId<T>(harness, messageId)).ShouldBeTrue(
            $"a published {typeof(T).Name} was never consumed, so this barrier cannot say the " +
            "next publish is ordered after it — an unfenced publish is a race the runner loses " +
            "under load, and it fails a later assertion wearing the wrong component's name.");
    }

    internal static Task<bool> Sent<T>(ITestHarness harness, Func<T, bool> match)
        where T : class =>
        harness.Sent.Any<T>(m => match(m.Context.Message), TestContext.Current.CancellationToken);

    internal static Task<bool> Consumed<T>(ITestHarness harness, Func<T, bool> match)
        where T : class =>
        harness.Consumed.Any<T>(m => match(m.Context.Message), TestContext.Current.CancellationToken);

    private static Task<bool> ConsumedWithId<T>(ITestHarness harness, Guid? messageId)
        where T : class =>
        harness.Consumed.Any<T>(m => m.Context.MessageId == messageId, TestContext.Current.CancellationToken);

    /// <summary>A negative read as of now, leaving the harness's one inactivity bound unspent (§12.5).</summary>
    /// <remarks>
    /// A deadline would fail open, since a late saga fits inside it; after <see cref="Publish"/> the saga has
    /// consumed the message, which is the point in time the negative is read at.
    /// </remarks>
    internal static Task<bool> NotYetSent<T>(ITestHarness harness, Func<T, bool> match)
        where T : class =>
        harness.Sent.Any<T>(m => match(m.Context.Message), Spent());

    /// <summary><see cref="NotYetSent"/>'s sibling over the published list.</summary>
    internal static Task<bool> NotYetPublished<T>(ITestHarness harness, Func<T, bool> match)
        where T : class =>
        harness.Published.Any<T>(m => match(m.Context.Message), Spent());

    /// <summary>A token cancelled on construction, with no source to be disposed under a caller.</summary>
    internal static CancellationToken Spent() => new(canceled: true);

    /// <summary>The exception each recorded consume of <typeparamref name="T"/> ended with, or null.</summary>
    /// <remarks>
    /// <see cref="Consumed"/> records a faulted consume too (§9.6); <see cref="Spent"/> keeps this read from
    /// spending the inactivity bound every later assertion shares.
    /// </remarks>
    internal static IEnumerable<Exception?> ConsumeFaults<T>(ITestHarness harness)
        where T : class =>
        harness.Consumed.Select<T>(Spent()).Select(m => m.Exception);
}
