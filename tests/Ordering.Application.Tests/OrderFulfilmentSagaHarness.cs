using Common.Contracts;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Infrastructure.Messaging;
using Shouldly;
using Xunit;

namespace Ordering.Application.Tests;

/// <summary>
/// What §12.5's suite shares: §9.6's saga driven end to end over
/// MassTransit's in-memory harness, with no infrastructure at all. It lives
/// here rather than in <c>Ordering.Api.Tests</c> because the suites that need
/// Docker pay for a container set each (§12.4), and this one needs none.
/// </summary>
/// <remarks>
/// The saga is <c>Ordering.Infrastructure</c>'s, which this project already
/// references for <c>AddOrderingInfrastructure</c>; §4.2's gate binds
/// <c>Ordering.Application</c>, not its tests.
/// </remarks>
internal static class OrderFulfilmentSagaHarness
{
    /// <summary>
    /// §12.5's two bounds, stated rather than inherited, and stated once per
    /// harness rather than per test.
    /// </summary>
    /// <remarks>
    /// An <c>Any(…)</c> ends at the earliest of a match, the inactivity bound
    /// (1.2 s by default, from the last bus activity), the test bound (from the
    /// call) and the caller's token. Inherit either and a saturated runner
    /// fails the suite wearing the assertion's own message. The ceiling is kept
    /// clear of the bound meant to fire, so which one reported a failure is
    /// never a detail of how long a publish took.
    /// </remarks>
    internal static readonly TimeSpan InactivityTimeout = TimeSpan.FromSeconds(10);

    internal static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(60);

    internal static readonly Guid Customer = Guid.Parse("2a1c9e64-77b1-4b0e-9a3e-6d9c1c2f5a11");

    /// <summary>
    /// The registration every test shares, with the scheduler §12.5's sample
    /// omits.
    /// </summary>
    /// <remarks>
    /// <c>Initially</c> arms <c>StockTimeout</c>, so the first
    /// <c>OrderPlaced</c> reaches for a scheduler nothing else puts on the
    /// pipeline; without the two scheduler lines the saga faults to the error
    /// queue and every waiting assertion fails as a timeout rather than an
    /// error. They are the lines production uses (ADR-021): the in-memory
    /// transport implements the delay itself, so the transports differ and the
    /// registration under test does not.
    /// </remarks>
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

    /// <summary>
    /// Publishes, and does not return until that message has been consumed — by
    /// the saga, or by whatever consumer is bound to it. A fault counts as
    /// consumed, so this is a claim about ordering and never about outcome.
    /// </summary>
    /// <remarks>
    /// A publish returns when the message reaches the transport, not when the
    /// saga has consumed it, so two unfenced publishes are a race the runner
    /// loses under load, failing a later assertion wearing the wrong
    /// component's name. The barrier is here rather than at the call sites
    /// because per-site discipline fails open. It waits on this message's id,
    /// not its type, so a second delivery of one type is fenced too; the id is
    /// read from the send context because a scheduled expiry has no envelope,
    /// and for a contract the two are one value (§9.1). Only a type no consumer
    /// takes spends the inactivity bound, and that is a real defect reported
    /// where it happened.
    /// </remarks>
    internal static async Task Publish<T>(ITestHarness harness, T message)
        where T : class
    {
        Guid? messageId = null;
        await harness.Bus.Publish(
            message,
            context =>
            {
                // §9.1: body, row, header and inbox key are one GUID, and
                // IIntegrationEvent says CorrelationId follows the same rule.
                // Letting MassTransit mint its own would give every event two
                // identities, and nothing here would fail for it.
                if (message is IIntegrationEvent integrationEvent)
                {
                    context.MessageId = integrationEvent.MessageId;
                    context.CorrelationId = integrationEvent.CorrelationId;
                }

                // A scheduled expiry is not a contract and has no envelope:
                // §3.2 lists it in no column and §4.3 keeps it private to the
                // saga. The send context is the one handle both kinds carry,
                // and the wait reads it.
                messageId = context.MessageId;
            },
            TestContext.Current.CancellationToken);

        // Unset, this degrades into a type-level wait — null == null matches
        // the first consume of T and fences nothing — so it fails loudly
        // instead.
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

    /// <summary>
    /// <see cref="Consumed"/> over the send context's message id, which is what
    /// <see cref="Publish"/> needs and no test does.
    /// </summary>
    private static Task<bool> ConsumedWithId<T>(ITestHarness harness, Guid? messageId)
        where T : class =>
        harness.Consumed.Any<T>(m => m.Context.MessageId == messageId, TestContext.Current.CancellationToken);

    /// <summary>
    /// A negative assertion read as of now: no wait, no deadline for a late
    /// saga to hide inside, and the harness's one shared inactivity token left
    /// unspent for whatever follows (§12.5).
    /// </summary>
    /// <remarks>
    /// A deadline would fail open: a window is something a late-sending saga
    /// fits inside, so "not yet" needs a point in time to be false at. Where
    /// the negative follows a publish, <see cref="Publish"/> is that point,
    /// since it returns only once the saga has consumed the message; a negative
    /// asserted anywhere else still needs the caller to pin the moment. §12.5
    /// permits a trailing negative to simply wait, but waiting costs the full
    /// inactivity bound for an answer already knowable, so every negative here
    /// goes through this or <see cref="NotYetPublished"/>.
    /// </remarks>
    internal static Task<bool> NotYetSent<T>(ITestHarness harness, Func<T, bool> match)
        where T : class =>
        harness.Sent.Any<T>(m => match(m.Context.Message), Spent());

    /// <summary>
    /// <see cref="NotYetSent"/>'s sibling over the published list, for the one
    /// negative here that asserts a command was not published.
    /// </summary>
    internal static Task<bool> NotYetPublished<T>(ITestHarness harness, Func<T, bool> match)
        where T : class =>
        harness.Published.Any<T>(m => match(m.Context.Message), Spent());

    /// <summary>
    /// An already-cancelled token, so an assertion reads the record as of now
    /// rather than waiting for something the test has just proved will not
    /// come.
    /// </summary>
    /// <remarks>
    /// The constructor, not a cancelled <c>CancellationTokenSource</c>: §12.5's
    /// source form is written inside the test, where the <c>using</c> scope
    /// outlives the assertion. Behind a helper the source is disposed on
    /// return, and a token whose source is disposed still reports
    /// <c>IsCancellationRequested</c> while throwing
    /// <c>ObjectDisposedException</c> from <c>Register</c>. Cancelled on
    /// construction, it owns nothing that can be disposed from under a caller.
    /// </remarks>
    internal static CancellationToken Spent() => new(canceled: true);

    /// <summary>
    /// The exception each recorded consume of <typeparamref name="T"/> ended
    /// with, or null where it ended cleanly.
    /// </summary>
    /// <remarks>
    /// The harness records a consume whether the pipeline threw or not, so
    /// <see cref="Consumed"/> answers "did it arrive" and never "what happened
    /// to it"; a saga event that no longer applies faults by default (§9.6),
    /// and every negative in this file stays green through it. Read the list
    /// only once the delivery it asks about has been pinned, which for a
    /// published message <see cref="Publish"/> did. <see cref="Spent"/> is
    /// load-bearing: the token-less overload enumerates until the harness's one
    /// shared inactivity bound, and a mid-test read that spends it makes every
    /// assertion after it answer immediately and falsely.
    /// </remarks>
    internal static IEnumerable<Exception?> ConsumeFaults<T>(ITestHarness harness)
        where T : class =>
        harness.Consumed.Select<T>(Spent()).Select(m => m.Exception);
}
