using System.Net;
using System.Net.Http.Json;
using Common.Contracts.Catalog.V1;
using Common.Infrastructure.Inbox;
using Ordering.Application.Orders.PlaceOrder;
using Ordering.Infrastructure.Messaging;
using Ordering.TestSupport;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>A Catalog event over a real broker, through the real endpoint and §6.6's projection.</summary>
/// <remarks>The harness replaces the endpoint's callback, as <see cref="MessagingRegistrationTests"/> shows.</remarks>
[Collection(nameof(IntegrationCollection))]
public sealed class CatalogEventEndpointTests(ServiceFixture fixture) : IAsyncLifetime
{
    private static readonly Guid Caller = Guid.Parse("44444444-4444-4444-4444-444444444444");

    /// <summary>A broker round trip on a loaded runner, bounded since an unbound endpoint never arrives.</summary>
    private static readonly TimeSpan DeliveryBudget = TimeSpan.FromSeconds(30);

    /// <summary>Every message id this test published, so <see cref="DisposeAsync"/> can drain them.</summary>
    private readonly List<Guid> _published = [];

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    /// <summary>Waits for each inbox row, the delivery's last write (§9.5), before the next test resets.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (Guid messageId in _published)
        {
            await Eventually(
                async () => (await InboxRowsAsync(messageId)).Count,
                expected: 1,
                because: "a delivery still running when the next test resets the schema is a flake in " +
                    "that test rather than a failure in this one");
        }
    }

    [Fact]
    public async Task A_published_product_reaches_the_projection_over_the_broker()
    {
        Guid product = Guid.CreateVersion7();
        var messageId = Guid.CreateVersion7();

        await PublishAsync(product, 19.99m, "EUR", messageId);

        await Eventually(
            () => fixture.ScalarAsync<int>(
                "SELECT Value = COUNT(*) FROM ordering.ProductPrices WHERE ProductId = {0}",
                product),
            expected: 1,
            because: "the endpoint declared in AddMassTransitMessaging is what binds ProductPublished to " +
                "the projection — a consumer registered and never bound looks exactly like this until the " +
                "budget runs out");

        (await fixture.ScalarAsync<decimal>(
            "SELECT Value = Amount FROM ordering.ProductPrices WHERE ProductId = {0}",
            product))
            .ShouldBe(19.99m);
    }

    [Fact]
    public async Task One_delivery_is_recorded_once_on_the_named_queue()
    {
        // A row on the named queue says the message arrived through the filtered endpoint rather than one
        // MassTransit manufactured; one row says the event is bound once.
        Guid product = Guid.CreateVersion7();
        var messageId = Guid.CreateVersion7();

        await PublishAsync(product, 19.99m, "EUR", messageId);

        await Eventually(
            async () => (await InboxRowsAsync(messageId)).Count,
            expected: 1,
            because: "a delivery that leaves no inbox row reached an endpoint with no filter on it, which " +
                "§9.8 permits only for the saga and only in writing");

        // Held past the first sighting, because the count claim is about a second row.
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        IReadOnlyList<InboxMessage> rows = await InboxRowsAsync(messageId);

        rows.Count.ShouldBe(1);
        rows[0].Endpoint.ShouldBe(
            DependencyInjection.CatalogEventsQueue,
            "§9.4 and §9.8 both print this queue name, and the saga (§9.6) will address the other two by " +
            "name — a renamed queue is a subscription that silently stops arriving");
    }

    [Fact]
    public async Task A_product_becomes_orderable_once_its_price_has_been_projected()
    {
        // §6.4's write path prices from the projected fact with no network call in the transaction.
        Guid product = Guid.CreateVersion7();

        (await PlaceAsync(product)).StatusCode.ShouldBe(
            HttpStatusCode.UnprocessableEntity,
            "the arrange half is a claim too: without it a green assertion below could be a price left " +
            "over from another test rather than the one this published");

        await PublishAsync(product, 19.99m, "EUR", Guid.CreateVersion7());

        await Eventually(
            () => fixture.ScalarAsync<int>(
                "SELECT Value = COUNT(*) FROM ordering.ProductPrices WHERE ProductId = {0}",
                product),
            expected: 1,
            because: "nothing downstream can be asserted until the projection has run");

        HttpResponseMessage response = await PlaceAsync(product);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        Guid id = await response.Content.ReadFromJsonAsync<Guid>(TestContext.Current.CancellationToken);

        (await fixture.ScalarAsync<decimal>(
            "SELECT Value = UnitPriceAmount FROM ordering.OrderLines WHERE OrderId = {0}",
            id))
            .ShouldBe(19.99m, "the order is priced from the projection and never from the request");
    }

    /// <summary>The PriceChanged binding over the same queue, since registration alone is not a binding.</summary>
    [Fact]
    public async Task A_price_change_reaches_the_projection_over_the_broker()
    {
        Guid product = Guid.CreateVersion7();

        await PublishAsync(product, 19.99m, "EUR", Guid.CreateVersion7());
        await Eventually(
            () => AmountPenceAsync(product),
            expected: 1999,
            because: "the arrange half has to land before the assert half can mean anything");

        await PublishChangeAsync(product, 24.99m, "EUR");

        await Eventually(
            () => AmountPenceAsync(product),
            expected: 2499,
            because: "PriceChanged has its own ConfigureConsumer line, and nothing but this drives it " +
                "over the queue that line names");
    }

    [Fact]
    public async Task A_discontinuation_reaches_the_projection_over_the_broker()
    {
        Guid product = Guid.CreateVersion7();

        await PublishAsync(product, 19.99m, "EUR", Guid.CreateVersion7());
        await Eventually(
            () => AmountPenceAsync(product),
            expected: 1999,
            because: "the arrange half has to land before the assert half can mean anything");

        await PublishDiscontinuedAsync(product);

        await Eventually(
            () => fixture.ScalarAsync<int>(
                """
                SELECT Value = COUNT(*)
                FROM ordering.ProductPrices
                WHERE ProductId = {0}
                    AND IsAvailable = 0
                """,
                product),
            expected: 1,
            because: "ProductDiscontinued has its own ConfigureConsumer line too, and it is the third " +
                "binding no other test reaches");
    }

    /// <summary>Publishes with the envelope's id as the transport id, one GUID (§9.1).</summary>
    private async Task PublishAsync(Guid product, decimal amount, string currency, Guid messageId)
    {
        ProductPublished published = new()
        {
            MessageId = messageId,
            CorrelationId = Guid.CreateVersion7(),
            OccurredAt = DateTimeOffset.UtcNow,
            ProductId = product,
            Name = "A product",
            ThumbnailUrl = null,
            Amount = amount,
            Currency = currency
        };

        _published.Add(messageId);

        await fixture.Factory.Services
            .GetRequiredService<IBus>()
            .Publish(
                published,
                c =>
                {
                    c.MessageId = messageId;
                    c.CorrelationId = published.CorrelationId;
                },
                TestContext.Current.CancellationToken);
    }

    private async Task PublishChangeAsync(Guid product, decimal amount, string currency)
    {
        var messageId = Guid.CreateVersion7();

        PriceChanged changed = new()
        {
            MessageId = messageId,
            CorrelationId = Guid.CreateVersion7(),
            // Strictly later than the publish, which the projection's guard requires.
            OccurredAt = DateTimeOffset.UtcNow.AddMinutes(1),
            ProductId = product,
            Amount = amount,
            Currency = currency
        };

        _published.Add(messageId);

        await fixture.Factory.Services
            .GetRequiredService<IBus>()
            .Publish(
                changed,
                c =>
                {
                    c.MessageId = messageId;
                    c.CorrelationId = changed.CorrelationId;
                },
                TestContext.Current.CancellationToken);
    }

    private async Task PublishDiscontinuedAsync(Guid product)
    {
        var messageId = Guid.CreateVersion7();

        ProductDiscontinued discontinued = new()
        {
            MessageId = messageId,
            CorrelationId = Guid.CreateVersion7(),
            OccurredAt = DateTimeOffset.UtcNow.AddMinutes(1),
            ProductId = product
        };

        _published.Add(messageId);

        await fixture.Factory.Services
            .GetRequiredService<IBus>()
            .Publish(
                discontinued,
                c =>
                {
                    c.MessageId = messageId;
                    c.CorrelationId = discontinued.CorrelationId;
                },
                TestContext.Current.CancellationToken);
    }

    /// <summary>The amount in minor units, because <see cref="Eventually"/> polls an <c>int</c>.</summary>
    private Task<int> AmountPenceAsync(Guid product) =>
        fixture.ScalarAsync<int>(
            """
            SELECT Value = COALESCE(CAST(MAX(Amount) * 100 AS int), 0)
            FROM ordering.ProductPrices
            WHERE ProductId = {0}
            """,
            product);

    private async Task<IReadOnlyList<InboxMessage>> InboxRowsAsync(Guid messageId) =>
        [.. (await fixture.InboxAsync()).Where(m => m.MessageId == messageId)];

    private Task<HttpResponseMessage> PlaceAsync(Guid product)
    {
        HttpClient client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, Caller.ToString());
        client.DefaultRequestHeaders.Add(TestAuthHandler.PermissionsHeader, OrderingPermissions.Write);

        return client.PostAsJsonAsync(
            "/v1/orders",
            new PlaceOrderCommand(
                Guid.CreateVersion7(),
                [new PlaceOrderItem(product, 1)],
                new AddressDto("1 Test Street", null, "Almaty", "050000", "KZ"),
                "EUR"),
            TestContext.Current.CancellationToken);
    }

    /// <summary>Polls rather than sleeps, and fails with the last value it saw.</summary>
    private static async Task Eventually(Func<Task<int>> read, int expected, string because)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + DeliveryBudget;
        int actual = 0;

        while (DateTimeOffset.UtcNow < deadline)
        {
            actual = await read();

            if (actual == expected)
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        }

        actual.ShouldBe(expected, because);
    }
}
