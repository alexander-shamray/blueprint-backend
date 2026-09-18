using Common.Contracts.Inventory.V1;
using Common.Contracts.Ordering.V1;
using Inventory.TestSupport;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
// Aliased because Common.Application has a DependencyInjection too, and the
// queue name this suite asserts on is the messaging one's.
using MessagingRegistration = Inventory.Infrastructure.Messaging.DependencyInjection;

namespace Inventory.Api.Tests;

/// <summary>
/// <c>inventory-commands</c> (§9.6): the mappers, the retry policy's
/// exclusion, the inbox filter and both handlers, driven over the real
/// broker rather than through <see cref="MessagingRegistrationTests"/>'
/// in-memory harness — that suite proves the registration composes, and
/// stops at the queue address; nothing else exercises the topology a saga
/// actually sends into.
/// </summary>
[Collection(nameof(IntegrationCollection))]
public sealed class InventoryCommandEndpointTests(ServiceFixture fixture) : IAsyncLifetime
{
    /// <summary>
    /// <see cref="OrderingCommandEndpointTests"/>'s budget, for its reason: a
    /// broker round trip on a runner holding other container sets, and
    /// bounded because an endpoint that binds nothing never arrives late — it
    /// never arrives.
    /// </summary>
    private static readonly TimeSpan DeliveryBudget = TimeSpan.FromSeconds(30);

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_reserve_over_the_queue_takes_the_stock_and_stages_StockReserved()
    {
        var product = Guid.CreateVersion7();
        await SeedStock(product, 3);
        var order = Guid.CreateVersion7();

        await SendAsync(new ReserveStock(order, [new StockLine(product, 2)]));

        await EventuallyStatus(order, "Reserved");
        (await Available(product)).ShouldBe(1);
        (await fixture.OutboxAsync())
            .Count(r => r.MessageType.Contains("StockReserved", StringComparison.Ordinal))
            .ShouldBe(1);
        (await fixture.OutboxAsync())
            .Count(r => r.MessageType.Contains("StockLevelChanged", StringComparison.Ordinal))
            .ShouldBe(1);
    }

    [Fact]
    public async Task A_short_reserve_commits_Failed_and_moves_no_stock()
    {
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();
        await SeedStock(a, 5);
        await SeedStock(b, 0);
        var order = Guid.CreateVersion7();

        await SendAsync(new ReserveStock(order, [new StockLine(a, 1), new StockLine(b, 1)]));

        await EventuallyStatus(order, "Failed");
        (await Available(a)).ShouldBe(5);
        (await fixture.OutboxAsync())
            .Count(r => r.MessageType.Contains("StockReservationFailed", StringComparison.Ordinal))
            .ShouldBe(1);
    }

    [Fact]
    public async Task A_release_before_its_reserve_leaves_a_tombstone_that_refuses_the_reserve()
    {
        var product = Guid.CreateVersion7();
        await SeedStock(product, 3);
        var order = Guid.CreateVersion7();

        await SendAsync(new ReleaseStock(order));
        await EventuallyStatus(order, "Released");
        await SendAsync(new ReserveStock(order, [new StockLine(product, 1)]));

        await Eventually(
            async () => (await fixture.OutboxAsync())
                .Count(r => r.MessageType.Contains("StockReleased", StringComparison.Ordinal)),
            expected: 2,
            because: "ADR-024: the tombstone answers the refused reserve with the same postcondition");
        (await Available(product)).ShouldBe(3, "nothing was held");
        (await fixture.OutboxAsync())
            .ShouldNotContain(r => r.MessageType.Contains("StockReserved", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_release_of_a_held_reservation_gives_the_stock_back()
    {
        var product = Guid.CreateVersion7();
        await SeedStock(product, 3);
        var order = Guid.CreateVersion7();
        await SendAsync(new ReserveStock(order, [new StockLine(product, 2)]));
        await EventuallyStatus(order, "Reserved");

        await SendAsync(new ReleaseStock(order));

        await EventuallyStatus(order, "Released");
        (await Available(product)).ShouldBe(3);
    }

    [Fact]
    public async Task A_release_of_a_failed_reservation_publishes_the_postcondition_and_moves_nothing()
    {
        var a = Guid.CreateVersion7();
        await SeedStock(a, 0);
        var order = Guid.CreateVersion7();
        await SendAsync(new ReserveStock(order, [new StockLine(a, 1)]));
        await EventuallyStatus(order, "Failed");

        await SendAsync(new ReleaseStock(order));

        await Eventually(
            async () => (await fixture.OutboxAsync())
                .Count(r => r.MessageType.Contains("StockReleased", StringComparison.Ordinal)),
            expected: 1,
            because: "section 5's table: a Failed row still answers with the postcondition");
        (await StatusAsync(order)).ShouldBe("Failed");
        (await Available(a)).ShouldBe(0);
    }

    [Fact]
    public async Task A_second_release_of_a_released_reservation_publishes_again_and_returns_nothing_twice()
    {
        var product = Guid.CreateVersion7();
        await SeedStock(product, 3);
        var order = Guid.CreateVersion7();
        await SendAsync(new ReserveStock(order, [new StockLine(product, 2)]));
        await EventuallyStatus(order, "Reserved");
        await SendAsync(new ReleaseStock(order));
        await EventuallyStatus(order, "Released");

        await SendAsync(new ReleaseStock(order));

        await Eventually(
            async () => (await fixture.OutboxAsync())
                .Count(r => r.MessageType.Contains("StockReleased", StringComparison.Ordinal)),
            expected: 2,
            because: "ADR-024's first guarantee holds on the second release as on the first");
        (await Available(product)).ShouldBe(3, "the lines were given back once, not twice");
    }

    [Theory]
    [InlineData(0, false, 1)]
    [InlineData(OrderLimits.MaxQuantity + 1, false, 1)]
    [InlineData(1, true, 1)]
    [InlineData(1, false, OrderLimits.MaxLines + 1)]
    public async Task A_malformed_reserve_is_a_contract_fault_and_is_not_retried(
        int quantity,
        bool emptyProduct,
        int lineCount)
    {
        var order = Guid.CreateVersion7();
        StockLine[] lines =
        [
            .. Enumerable.Range(0, lineCount)
                .Select(_ => new StockLine(emptyProduct ? Guid.Empty : Guid.CreateVersion7(), quantity))
        ];

        // drain: false, because a message the mapper refuses never reaches the
        // inbox filter and so leaves no row for the default drain to wait on.
        await SendAsync(new ReserveStock(order, lines), drain: false);

        // **The sentinel bounds this wait; it does not prove delivery order.**
        // The endpoint sets no ConcurrentMessageLimit, so MassTransit's
        // prefetch lets both messages be in flight together and the sentinel
        // could finish first. What makes "no row" a practical verdict rather
        // than a race is the head start: seeding the sentinel's stock and
        // sending it happen after the malformed send has already reached the
        // broker, so by the time the sentinel's own row exists the malformed
        // message has almost certainly been consumed too — a bound that
        // tracks the machine rather than a guarantee.
        var sentinelProduct = Guid.CreateVersion7();
        await SeedStock(sentinelProduct, 1);
        var sentinelOrder = Guid.CreateVersion7();
        await SendAsync(new ReserveStock(sentinelOrder, [new StockLine(sentinelProduct, 1)]));
        await EventuallyStatus(sentinelOrder, "Reserved");

        (await fixture.ScalarAsync<int>(
                "SELECT Value = COUNT(*) FROM inventory.Reservations WHERE OrderId = {0}", order))
            .ShouldBe(0, "nothing was written for this order, and the sentinel behind it on the same " +
                "queue was consumed — CommandMappersTests ties that to the mapper's own refusal rather " +
                "than to the validator's, which this alone cannot tell apart");
    }

    [Fact]
    public async Task A_malformed_release_is_a_contract_fault_and_is_not_retried()
    {
        // On the message path a validator failure would propagate out of
        // CommandConsumer as a fault and be retried, where a domain
        // rejection is acked, counted and logged instead — so the mapper
        // refuses this itself, with ContractMappingException excluded from
        // retry (§9.8), and it reaches the error queue on the first attempt.
        // ReleaseStock carries no other bound to violate, so an empty order
        // id is the one shape ReleaseStockMapper has to refuse.
        var order = Guid.Empty;

        await SendAsync(new ReleaseStock(order), drain: false);

        var sentinelProduct = Guid.CreateVersion7();
        await SeedStock(sentinelProduct, 1);
        var sentinelOrder = Guid.CreateVersion7();
        await SendAsync(new ReserveStock(sentinelOrder, [new StockLine(sentinelProduct, 1)]));
        await EventuallyStatus(sentinelOrder, "Reserved");

        (await fixture.ScalarAsync<int>(
                "SELECT Value = COUNT(*) FROM inventory.Reservations WHERE OrderId = {0}", order))
            .ShouldBe(0, "nothing was written for this order, and the sentinel behind it on the same " +
                "queue was consumed — CommandMappersTests ties that to ReleaseStockMapper's own " +
                "refusal rather than to the validator's, which this alone cannot tell apart");
    }

    private Task SeedStock(Guid product, int available) =>
        fixture.ExecuteAsync(
            "INSERT INTO inventory.StockItems (ProductId, Available, Reserved, UpdatedAt) " +
            "VALUES ({0}, {1}, 0, SYSDATETIMEOFFSET())",
            product,
            available);

    private Task<int> Available(Guid product) =>
        fixture.ScalarAsync<int>(
            "SELECT Value = Available FROM inventory.StockItems WHERE ProductId = {0}", product);

    private Task<string> StatusAsync(Guid orderId) =>
        fixture.ScalarAsync<string>(
            "SELECT Value = Status FROM inventory.Reservations WHERE OrderId = {0}", orderId);

    /// <summary>
    /// Polls <see cref="StatusAsync"/> until it reads <paramref name="expected"/>
    /// or the budget elapses. No row yet is not a failure — the handler has not
    /// committed — so it is read as "not yet" rather than let the missing-row
    /// exception end the poll early.
    /// </summary>
    private async Task EventuallyStatus(Guid orderId, string expected)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + DeliveryBudget;
        string actual = "";

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                actual = await StatusAsync(orderId);
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

    /// <summary>
    /// Sends to <c>inventory-commands</c> by address, exactly as §9.6's saga
    /// does. <paramref name="drain"/> at its default waits on the inbox row
    /// the delivery writes, so the caller observes the handler's own
    /// transaction rather than a send that has not yet been consumed.
    /// </summary>
    /// <param name="drain">
    /// False only where no inbox row will ever be written — the
    /// malformed-contract case: <c>InboxFilter</c> commits its row after the
    /// consumer returns, and a mapper that throws means it never does, so
    /// waiting for that row would spend the whole delivery budget proving
    /// something the caller already asserts a different way.
    /// </param>
    private async Task SendAsync<T>(T command, bool drain = true)
        where T : class
    {
        // IBus, not the scoped ISendEndpointProvider: IBus is registered as
        // a singleton and is itself an ISendEndpointProvider, so it resolves
        // straight from the root provider — the same resolution
        // OrderingCommandEndpointTests uses.
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
