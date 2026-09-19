using Common.Contracts.Inventory.V1;
using Common.Contracts.Ordering.V1;
using Inventory.TestSupport;
using Shouldly;
using Xunit;

namespace Inventory.Api.Tests;

/// <summary>
/// <c>inventory-commands</c> (§9.6): the mappers, the retry policy's
/// exclusion, the inbox filter and both handlers, driven over the real
/// broker rather than an in-memory harness, because only a real delivery
/// exercises the topology a saga actually sends into.
/// </summary>
[Collection(nameof(IntegrationCollection))]
public sealed class InventoryCommandEndpointTests(ServiceFixture fixture) : IAsyncLifetime
{
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
    public async Task A_second_reserve_for_a_Reserved_order_answers_again_and_moves_no_stock()
    {
        var product = Guid.CreateVersion7();
        await SeedStock(product, 3);
        var order = Guid.CreateVersion7();
        await SendAsync(new ReserveStock(order, [new StockLine(product, 2)]));
        await EventuallyStatus(order, "Reserved");

        await SendAsync(new ReserveStock(order, [new StockLine(product, 2)]));

        await Eventually(
            async () => (await fixture.OutboxAsync())
                .Count(r => r.MessageType.Contains("StockReserved", StringComparison.Ordinal)),
            expected: 2,
            because: "section 4's table: a Reserved row still answers again");
        (await Available(product)).ShouldBe(1, "the second reserve took no lines");
        (await fixture.OutboxAsync())
            .Count(r => r.MessageType.Contains("StockLevelChanged", StringComparison.Ordinal))
            .ShouldBe(1, "the second reserve staged no level");
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
        (await fixture.OutboxAsync())
            .Count(r => r.MessageType.Contains("StockLevelChanged", StringComparison.Ordinal))
            .ShouldBe(0, "the savepoint rolled the short line's decrement back before anything committed");
    }

    [Fact]
    public async Task A_second_reserve_for_a_Failed_order_answers_again_with_the_same_ids()
    {
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();
        await SeedStock(a, 5);
        await SeedStock(b, 0);
        var order = Guid.CreateVersion7();
        await SendAsync(new ReserveStock(order, [new StockLine(a, 1), new StockLine(b, 1)]));
        await EventuallyStatus(order, "Failed");

        await SendAsync(new ReserveStock(order, [new StockLine(a, 1), new StockLine(b, 1)]));

        await Eventually(
            async () => (await fixture.OutboxAsync())
                .Count(r => r.MessageType.Contains("StockReservationFailed", StringComparison.Ordinal)),
            expected: 2,
            because: "section 4's table: a Failed row still answers again");
        (await Available(a)).ShouldBe(5, "the second reserve took no lines");
        (await fixture.OutboxAsync())
            .Where(r => r.MessageType.Contains("StockReservationFailed", StringComparison.Ordinal))
            .ShouldAllBe(r => r.Payload.Contains(b.ToString(), StringComparison.Ordinal)
                && !r.Payload.Contains(a.ToString(), StringComparison.Ordinal),
                "both answers name the line that was short, and neither names the one that was not");
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

        // The sentinel bounds this wait; it does not prove delivery order.
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

    [Fact]
    public async Task Two_orders_for_the_last_unit_leave_one_Reserved_and_one_Failed()
    {
        var product = Guid.CreateVersion7();
        await SeedStock(product, 1);
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        await Task.WhenAll(
            SendAsync(new ReserveStock(first, [new StockLine(product, 1)])),
            SendAsync(new ReserveStock(second, [new StockLine(product, 1)])));

        await Eventually(
            () => fixture.ScalarAsync<int>(
                "SELECT Value = COUNT(*) FROM inventory.Reservations WHERE OrderId IN ({0}, {1})",
                first,
                second),
            expected: 2,
            because: "both commands are answered");
        string[] statuses = [await StatusAsync(first), await StatusAsync(second)];
        statuses.Count(s => s == "Reserved").ShouldBe(1);
        statuses.Count(s => s == "Failed").ShouldBe(1);
        (await Available(product)).ShouldBe(0);
    }

    // Thin forwarders onto ReservationTestSupport: the implementation lives
    // once there, and this class keeps its own name so its test bodies read
    // unchanged.
    private Task SeedStock(Guid product, int available) =>
        ReservationTestSupport.SeedStock(fixture, product, available);

    private Task<int> Available(Guid product) =>
        ReservationTestSupport.Available(fixture, product);

    private Task<string> StatusAsync(Guid orderId) =>
        ReservationTestSupport.StatusAsync(fixture, orderId);

    private Task EventuallyStatus(Guid orderId, string expected) =>
        ReservationTestSupport.EventuallyStatus(fixture, orderId, expected);

    private static Task Eventually(Func<Task<int>> read, int expected, string because) =>
        ReservationTestSupport.Eventually(read, expected, because);

    private Task SendAsync<T>(T command, bool drain = true)
        where T : class =>
        ReservationTestSupport.SendAsync(fixture, command, drain);
}
