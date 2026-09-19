using Inventory.TestSupport;
using Inventory.TestSupport.Outbox;
using Common.Infrastructure.Outbox;
using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Inventory.Api.Tests;

/// <summary>
/// §9.1's single-identity rule, checked where it is actually kept: on the
/// transport. Body, row, broker header and inbox key are one GUID, and
/// <c>DeliverAsync</c> copying the row's ids onto the published context is the
/// hop that makes the last two agree with the first two.
/// </summary>
/// <remarks>
/// The dispatcher is common code and this asserts a per-service host: it
/// replaces <c>IPublishEndpoint</c> inside a factory built over
/// <c>Inventory.Api</c>'s <c>Program</c>, so it answers whether this
/// service's own registration reaches the same code rather than assuming
/// another service's copy speaks for it.
/// <para>
/// A substitute for <c>IPublishEndpoint</c> rather than a harness: §12.4
/// refuses to bolt an <c>ITestHarness</c> onto this fixture, because it runs
/// the real host against the real broker on purpose and a harness would
/// replace the bus configuration the other tests exist to exercise. Capturing
/// the pipe costs one registration in one factory, disturbs nothing else, and
/// asserts the same thing.
/// </para>
/// </remarks>
[Collection(nameof(IntegrationCollection))]
public sealed class OutboxTransportIdentityTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Delivery_copies_the_rows_ids_onto_the_published_context()
    {
        var productId = Guid.CreateVersion7();
        OutboxMessage staged = OutboxRows.Broker(fixture, productId);
        await fixture.StageOutboxAsync(staged);

        // A host of its own, so the substitute replaces the real endpoint for
        // this test and for nothing else in the collection.
        using CapturingPublishFactory factory = new(fixture.ConnectionString);
        OutboxDispatcher dispatcher = factory.Services.GetRequiredService<OutboxDispatcher>();

        (await dispatcher.ProcessBatchAsync(TestContext.Current.CancellationToken)).ShouldBe(1);

        // Replay the pipe the dispatcher handed the endpoint against a context
        // that records what is set on it. This is the callback's whole body.
        PublishContext context = Substitute.For<PublishContext>();
        await factory.Captured.ShouldNotBeNull().Send(context);

        context.Received().MessageId = staged.MessageId;
        context.Received().CorrelationId = staged.CorrelationId;
    }

    private sealed class CapturingPublishFactory(string connectionString) : InventoryApiFactory(
        connectionString,
        "amqp://guest:guest@inventory-rabbit.invalid:5672")
    {
        public IPipe<PublishContext>? Captured { get; private set; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureServices(services =>
            {
                IPublishEndpoint endpoint = Substitute.For<IPublishEndpoint>();

                endpoint
                    .Publish(
                        Arg.Any<object>(),
                        Arg.Any<Type>(),
                        Arg.Any<IPipe<PublishContext>>(),
                        Arg.Any<CancellationToken>())
                    .Returns(call =>
                    {
                        Captured = call.Arg<IPipe<PublishContext>>();
                        return Task.CompletedTask;
                    });

                // Replaced, not added: the dispatcher resolves one endpoint,
                // and a second registration would leave MassTransit's real one
                // last and this substitute never called. The unreachable broker
                // in the base constructor is deliberate for the same reason —
                // nothing here should reach a transport.
                services.RemoveAll<IPublishEndpoint>();
                services.AddScoped(_ => endpoint);
            });
        }
    }
}
