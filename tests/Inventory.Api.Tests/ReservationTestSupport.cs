using Common.Contracts.Inventory.V1;
using Inventory.TestSupport;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
// Aliased because Common.Application has a DependencyInjection too, and the
// queue name this class sends by address is the messaging one's.
using MessagingRegistration = Inventory.Infrastructure.Messaging.DependencyInjection;

namespace Inventory.Api.Tests;

/// <summary>
/// The arrange-and-poll helpers a caller needs over one
/// <see cref="ServiceFixture"/> per test run. Each member names its fixture
/// explicitly rather than capturing one, because the fixture is per-test-class
/// state and this class holds none of its own; a caller keeps a private,
/// same-named forwarder onto it.
/// </summary>
internal static class ReservationTestSupport
{
    /// <summary>
    /// A broker round trip on a runner holding other container sets, bounded
    /// because an endpoint that binds nothing never arrives late — it never
    /// arrives.
    /// </summary>
    public static readonly TimeSpan DeliveryBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A client carrying <see cref="InventoryPermissions.Admin"/>, for the
    /// suites that need an admin client — lifted here rather than left as
    /// separate copies.
    /// </summary>
    public static HttpClient Admin(ServiceFixture fixture)
    {
        HttpClient client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());
        client.DefaultRequestHeaders.Add(TestAuthHandler.PermissionsHeader, InventoryPermissions.Admin);
        return client;
    }

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

    /// <summary>
    /// Polls <see cref="StatusAsync"/> until it reads <paramref name="expected"/>
    /// or the budget elapses. No row yet is not a failure — the handler has not
    /// committed — so it is read as "not yet" rather than let the missing-row
    /// exception end the poll early.
    /// </summary>
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

    /// <summary>
    /// Sends to <c>inventory-commands</c> by address, exactly as §9.6's saga
    /// does. <paramref name="drain"/> at its default waits on the inbox row
    /// the delivery writes, so the caller observes the handler's own
    /// transaction rather than a send that has not yet been consumed.
    /// </summary>
    /// <param name="fixture">The running host to send through.</param>
    /// <param name="command">The command to send.</param>
    /// <param name="drain">
    /// False only where no inbox row will ever be written — the malformed-
    /// contract case: <c>InboxFilter</c> commits its row after the consumer
    /// returns, and a mapper that throws means it never does, so waiting for
    /// that row would spend the whole delivery budget proving something the
    /// caller already asserts a different way.
    /// </param>
    public static async Task SendAsync<T>(ServiceFixture fixture, T command, bool drain = true)
        where T : class
    {
        // IBus, not the scoped ISendEndpointProvider: IBus is registered as a
        // singleton and is itself an ISendEndpointProvider, so it resolves
        // straight from the root provider.
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
