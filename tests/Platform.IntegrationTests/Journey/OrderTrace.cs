using System.Globalization;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Platform.IntegrationTests.Journey;

/// <summary>What the services hold about one order, read across all of them at one moment.</summary>
public sealed record OrderSnapshot(
    string OrderStatus,
    string SagaState,
    int Available,
    int Reserved,
    string PaymentStatus,
    string ShipmentStatus)
{
    /// <summary>The state a row that does not exist reads as.</summary>
    public const string None = "none";
}

/// <summary>Samples one order across every service, so the states it passed through are asserted legal (§12.1).</summary>
/// <remarks>
/// A sample can miss a short state, so legality is reachability: a state may follow any state the diagram reaches
/// it from (§9.6).
/// </remarks>
public sealed class OrderTrace : IAsyncDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(50);

    /// <summary>§5.4's order states, each with the states an order may be seen in after it.</summary>
    private static readonly Dictionary<string, string[]> OrderStates = new()
    {
        [OrderSnapshot.None] = ["AwaitingStock"],
        ["AwaitingStock"] = ["AwaitingPayment", "Cancelled"],
        ["AwaitingPayment"] = ["Confirmed", "Cancelled"],
        ["Confirmed"] = ["Shipped", "Cancelled"],
        ["Shipped"] = ["Delivered"],
        ["Delivered"] = [],
        ["Cancelled"] = []
    };

    /// <summary>§9.6's saga states: an instance that finalises is deleted, which reads as none.</summary>
    private static readonly Dictionary<string, string[]> SagaStates = new()
    {
        [OrderSnapshot.None] = ["AwaitingStock"],
        ["AwaitingStock"] = ["AwaitingPayment", "Compensating", OrderSnapshot.None],
        ["AwaitingPayment"] = ["AwaitingConfirmation", "Compensating"],
        ["AwaitingConfirmation"] = ["Confirmed", "Compensating", OrderSnapshot.None],
        ["Confirmed"] = [OrderSnapshot.None],
        ["Compensating"] = [OrderSnapshot.None],
        ["Final"] = [OrderSnapshot.None]
    };

    private readonly JourneyWorld _world;
    private readonly JourneyOrder _order;
    private readonly int _stocked;
    private readonly List<(TimeSpan At, OrderSnapshot Snapshot)> _seen = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly DateTimeOffset _start = DateTimeOffset.UtcNow;
    private Task _sampling = Task.CompletedTask;

    private OrderTrace(JourneyWorld world, JourneyOrder order, int stocked)
    {
        _world = world;
        _order = order;
        _stocked = stocked;
    }

    public IReadOnlyList<(TimeSpan At, OrderSnapshot Snapshot)> Seen
    {
        get
        {
            lock (_seen)
            {
                return [.. _seen];
            }
        }
    }

    /// <summary>The latest sample, which a convergence predicate reads.</summary>
    public OrderSnapshot? Latest
    {
        get
        {
            lock (_seen)
            {
                return _seen.Count == 0 ? null : _seen[^1].Snapshot;
            }
        }
    }

    /// <summary>Starts sampling <paramref name="order"/>, for a product that held <paramref name="stocked"/> in all.</summary>
    public static OrderTrace Start(JourneyWorld world, JourneyOrder order, int stocked)
    {
        OrderTrace trace = new(world, order, stocked);
        trace._sampling = Task.Run(() => trace.SampleAsync(trace._stop.Token));

        return trace;
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);

        await _stop.CancelAsync();
        try
        {
            await _sampling;
        }
        catch (OperationCanceledException)
        {
            // Stopping is how a sampler ends.
        }

        _stop.Dispose();
    }

    /// <summary>Waits for a predicate over the latest sample, which the sampler keeps current.</summary>
    public Task UntilAsync(Func<OrderSnapshot, bool> predicate, TimeSpan deadline, string what) =>
        Convergence.UntilAsync(
            () => Task.FromResult(Latest is { } latest && predicate(latest)),
            deadline,
            what);

    /// <summary>Every state seen followed from the one before it, and no stock went negative.</summary>
    public void ShouldHaveBeenLegal()
    {
        IReadOnlyList<OrderSnapshot> seen = [.. Seen.Select(s => s.Snapshot)];
        seen.ShouldNotBeEmpty("the sampler saw nothing, so legality was not asserted");

        AssertSequence("order", seen.Select(s => s.OrderStatus), OrderStates);
        AssertSequence("saga", seen.Select(s => s.SagaState), SagaStates);

        foreach (OrderSnapshot snapshot in seen)
        {
            snapshot.Available.ShouldBeGreaterThanOrEqualTo(0, "stock on hand is never negative");
            snapshot.Reserved.ShouldBeGreaterThanOrEqualTo(0, "a reservation is never negative");
            (snapshot.Available + snapshot.Reserved).ShouldBeLessThanOrEqualTo(
                _stocked,
                "stock is never created by an order");
        }
    }

    private static void AssertSequence(
        string what,
        IEnumerable<string> states,
        IReadOnlyDictionary<string, string[]> after)
    {
        string previous = OrderSnapshot.None;

        foreach (string state in states)
        {
            if (state == previous)
                continue;

            after.ShouldContainKey(state, $"{what} was seen in '{state}', which the diagram does not have");
            Reachable(previous, state, after).ShouldBeTrue(
                $"{what} was seen going from '{previous}' to '{state}', which the diagram cannot");

            previous = state;
        }
    }

    private static bool Reachable(string from, string to, IReadOnlyDictionary<string, string[]> after)
    {
        HashSet<string> visited = [from];
        Queue<string> pending = new([from]);

        while (pending.TryDequeue(out string? state))
        {
            if (!after.TryGetValue(state, out string[]? next))
                continue;

            foreach (string candidate in next)
            {
                if (candidate == to)
                    return true;

                if (visited.Add(candidate))
                    pending.Enqueue(candidate);
            }
        }

        return false;
    }

    private async Task SampleAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            OrderSnapshot now = await _world.SnapshotAsync(_order);

            lock (_seen)
            {
                if (_seen.Count == 0 || _seen[^1].Snapshot != now)
                    _seen.Add((DateTimeOffset.UtcNow - _start, now));
            }

            await Task.Delay(Interval, ct);
        }
    }
}

internal static class JourneyReads
{
    /// <summary>The order across the five services that hold part of it, in one read each.</summary>
    public static async Task<OrderSnapshot> SnapshotAsync(this JourneyWorld world, JourneyOrder order)
    {
        string orderStatus = await world.ScalarAsync<string?>(
            JourneyWorld.Ordering,
            "SELECT Status FROM ordering.Orders WHERE Id = @p0",
            order.Id) ?? OrderSnapshot.None;

        string saga = await world.ScalarAsync<string?>(
            JourneyWorld.Ordering,
            "SELECT CurrentState FROM ordering.OrderFulfilmentStates WHERE CorrelationId = @p0",
            order.Id) ?? OrderSnapshot.None;

        // One statement, so a release between two reads cannot show stock in two places at once.
        string[] stock = [.. (await world.ColumnAsync(
            JourneyWorld.Inventory,
            "SELECT CAST(Available AS varchar(11)) + ',' + CAST(Reserved AS varchar(11)) " +
            "FROM inventory.StockItems WHERE ProductId = @p0",
            order.Product)).SelectMany(row => row.Split(','))];

        int available = stock.Length == 2 ? int.Parse(stock[0], CultureInfo.InvariantCulture) : 0;
        int reserved = stock.Length == 2 ? int.Parse(stock[1], CultureInfo.InvariantCulture) : 0;

        string payment = await world.ScalarAsync<string?>(
            JourneyWorld.Payments,
            "SELECT Status FROM payments.PaymentIntents WHERE OrderId = @p0",
            order.Id) ?? OrderSnapshot.None;

        string shipment = await world.ScalarAsync<string?>(
            JourneyWorld.Shipping,
            "SELECT Status FROM shipping.Shipments WHERE OrderId = @p0",
            order.Id) ?? OrderSnapshot.None;

        return new OrderSnapshot(orderStatus, saga, available, reserved, payment, shipment);
    }

    /// <summary>Each notice the order produced, as its template key and status.</summary>
    public static Task<IReadOnlyList<string>> NoticesAsync(this JourneyWorld world, JourneyOrder order) =>
        world.ColumnAsync(
            JourneyWorld.Notifications,
            "SELECT TemplateKey + ':' + Status FROM notifications.NotificationLog WHERE OrderId = @p0",
            order.Id);

    /// <summary>The messages the relay holds for the order's customer.</summary>
    public static async Task<IReadOnlyList<MailpitMessage>> MailAsync(this JourneyWorld world, JourneyOrder order)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        List<MailpitMessage> mail = [];

        foreach (MailpitSummary summary in await world.Relay.MessagesAsync(ct))
        {
            if (summary.To.Any(to => to.Address == order.Mailbox))
                mail.Add(await world.Relay.MessageAsync(summary.Id, ct));
        }

        return mail;
    }

    /// <summary>The raised reviews an order holds, by reason (§9.6).</summary>
    public static Task<IReadOnlyList<string>> ReviewsAsync(this JourneyWorld world, JourneyOrder order) =>
        world.ColumnAsync(
            JourneyWorld.Ordering,
            "SELECT Reason FROM ordering.OrderReviews WHERE OrderId = @p0",
            order.Id);

    /// <summary>How many authorisations have been voided for the order.</summary>
    public static Task<int> RefundsAsync(this JourneyWorld world, JourneyOrder order) =>
        world.ScalarAsync<int>(
            JourneyWorld.Payments,
            "SELECT COUNT(*) FROM payments.Refunds WHERE OrderId = @p0",
            order.Id);

    /// <summary>The authorisations the provider was asked for in the customer's name.</summary>
    public static int AuthorisationsFor(this JourneyWorld world, JourneyOrder order) =>
        world.Provider.LogEntries.Count(e =>
            e.RequestMessage?.Path == "/v1/authorisations" &&
            e.RequestMessage.Body?.Contains(order.Customer.ToString("D"), StringComparison.Ordinal) == true);

    /// <summary>The bookings the carrier was asked for at the customer's address.</summary>
    public static int BookingsFor(this JourneyWorld world, JourneyOrder order) =>
        world.Carrier.LogEntries.Count(e =>
            e.RequestMessage?.Path == "/v1/shipments" &&
            e.RequestMessage.Body?.Contains(order.Customer.ToString("N"), StringComparison.Ordinal) == true);
}
