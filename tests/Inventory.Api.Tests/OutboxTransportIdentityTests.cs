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

/// <summary>§9.1's single identity on the transport: delivery copies the row's ids onto the context.</summary>
/// <remarks>A substitute rather than a harness, which §12.4 keeps off this real-broker fixture.</remarks>
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

        // A host of its own, so the substitute replaces the endpoint for this test alone.
        using CapturingPublishFactory factory = new(fixture.ConnectionString);
        OutboxDispatcher dispatcher = factory.Services.GetRequiredService<OutboxDispatcher>();

        (await dispatcher.ProcessBatchAsync(TestContext.Current.CancellationToken)).ShouldBe(1);

        // Replays the pipe the dispatcher handed the endpoint against a context that records what is set on it.
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

                services.RemoveAll<IPublishEndpoint>();
                services.AddScoped(_ => endpoint);
            });
        }
    }
}
