using System.Net.Http.Json;
using Common.Contracts.Inventory.V1;
using Inventory.TestSupport;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
// Aliased because Common.Application has a DependencyInjection too.
using MessagingRegistration = Inventory.Infrastructure.Messaging.DependencyInjection;

namespace Inventory.Api.Tests;

/// <summary>Arrange-and-poll helpers over a <see cref="ServiceFixture"/> the caller passes, holding no state.</summary>
internal static class ReservationTestSupport
{
    /// <summary>A broker round trip on a busy runner, bounded because an unbound endpoint never arrives.</summary>
    public static readonly TimeSpan DeliveryBudget = TimeSpan.FromSeconds(30);

    /// <summary>A client carrying <see cref="InventoryPermissions.Admin"/>.</summary>
    public static HttpClient Admin(ServiceFixture fixture) => Admin(fixture, Guid.CreateVersion7());

    /// <summary>The same client as <paramref name="caller"/>, for a test whose subject is §8.5's key.</summary>
    public static HttpClient Admin(ServiceFixture fixture, Guid caller)
    {
        HttpClient client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, caller.ToString());
        client.DefaultRequestHeaders.Add(TestAuthHandler.PermissionsHeader, InventoryPermissions.Admin);
        return client;
    }

    /// <summary>One reinstatement under <paramref name="commandId"/>, the caller's id for the attempt (§8.5).</summary>
    public static Task<HttpResponseMessage> ReinstateAsync(HttpClient client, Guid orderId, Guid commandId) =>
        client.PostAsJsonAsync(
            $"/v1/inventory/reservations/{orderId}/reinstate",
            new { commandId },
            TestContext.Current.CancellationToken);

    public static Task SeedStock(ServiceFixture fixture, Guid product, int available) =>
        fixture.ExecuteAsync(
            "INSERT INTO inventory.StockItems (ProductId, Available, Reserved, UpdatedAt) " +
            "VALUES ({0}, {1}, 0, SYSDATETIMEOFFSET())",
            product,
            available);

    public static Task<int> Available(ServiceFixture fixture, Guid product) =>
        fixture.ScalarAsync<int>(
            "SELECT Value = Available FROM inventory.StockItems WHERE ProductId = {0}", product);

    public static Task<string> StatusAsync(ServiceFixture fixture, Guid orderId) =>
        fixture.ScalarAsync<string>(
            "SELECT Value = Status FROM inventory.Reservations WHERE OrderId = {0}", orderId);

    /// <summary>Polls <see cref="StatusAsync"/> for <paramref name="expected"/>; no row yet is not yet.</summary>
    public static async Task EventuallyStatus(ServiceFixture fixture, Guid orderId, string expected)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + DeliveryBudget;
        string actual = "";

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                actual = await StatusAsync(fixture, orderId);
            }
            catch (InvalidOperationException)
            {
                actual = "";
            }

            if (actual == expected)
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        }

        actual.ShouldBe(expected, $"the reservation for {orderId} never reached {expected}");
    }

    public static async Task Eventually(Func<Task<int>> read, int expected, string because)
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

    /// <summary>Sends to <c>inventory-commands</c> by address, as §9.6's saga does.</summary>
    /// <param name="drain">Waits on the delivery's inbox row; false only where a mapper refusal writes none.</param>
    public static async Task SendAsync<T>(ServiceFixture fixture, T command, bool drain = true)
        where T : class
    {
        // IBus, not the scoped ISendEndpointProvider, because the singleton resolves from the root provider.
        ISendEndpoint endpoint = await fixture.Factory.Services
            .GetRequiredService<IBus>()
            .GetSendEndpoint(new Uri($"queue:{MessagingRegistration.CommandsQueue}"));

        var messageId = Guid.CreateVersion7();

        await endpoint.Send(command, c => c.MessageId = messageId, TestContext.Current.CancellationToken);

        if (drain)
        {
            await Eventually(
                async () => (await fixture.InboxAsync())
                    .Count(r => r.MessageId == messageId && r.Endpoint == MessagingRegistration.CommandsQueue),
                expected: 1,
                because: "the inbox row commits after the consumer returns, which is what makes this a " +
                    "wait for delivery rather than for the send");
        }
    }
}
