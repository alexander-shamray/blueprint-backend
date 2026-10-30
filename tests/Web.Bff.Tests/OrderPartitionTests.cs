using Dapper;
using Microsoft.Data.SqlClient;
using Shouldly;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>ADR-177: one order's events wait for each other in the host, and only one of them on its row.</summary>
/// <remarks>
/// The test holds the rows' locks itself and counts the sessions SQL Server shows blocked behind it, which is what
/// Query Store measured as the projection's lock wait (ADR-177).
/// </remarks>
[Collection(nameof(BffIntegrationCollection))]
public sealed class OrderPartitionTests(BffServiceFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset At = new(2026, 10, 30, 19, 0, 0, TimeSpan.Zero);

    // Long enough for a prefetched second delivery to reach SQL Server, which it does within milliseconds.
    private static readonly TimeSpan Watch = TimeSpan.FromSeconds(2);

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_despatch_and_a_delivery_published_together_never_both_wait_on_the_row()
    {
        Guid order = await PlacedAsync();

        await using RowHold hold = await RowHold.TakeAsync(fixture.ConnectionString, [order]);
        await fixture.SendAsync(OrderEvents.Dispatched(order, At.AddDays(1)));
        await fixture.SendAsync(OrderEvents.Delivered(order, At.AddDays(2)));

        await BffServiceFixture.WaitUntilAsync(async () => await hold.BlockedAsync() >= 1);

        DateTimeOffset until = DateTimeOffset.UtcNow + Watch;
        while (DateTimeOffset.UtcNow < until)
        {
            (await hold.BlockedAsync()).ShouldBe(1, "the second event waits in its partition, not on the row");
            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        }

        await hold.ReleaseAsync();
        await BffServiceFixture.WaitUntilAsync(async () =>
            (await fixture.OrderAsync(order)) is { DispatchedAt: not null, DeliveredAt: not null });
    }

    [Fact]
    public async Task Different_orders_events_still_wait_on_their_rows_side_by_side()
    {
        // Four, so that all of them hashing to one partition, the only way this fails, is negligible.
        Guid[] orders = [await PlacedAsync(), await PlacedAsync(), await PlacedAsync(), await PlacedAsync()];

        await using RowHold hold = await RowHold.TakeAsync(fixture.ConnectionString, orders);
        foreach (Guid order in orders)
            await fixture.SendAsync(OrderEvents.Dispatched(order, At.AddDays(1)));

        // Proves the endpoint runs concurrently, without which the first test passes for the wrong reason.
        await BffServiceFixture.WaitUntilAsync(async () => await hold.BlockedAsync() >= 2);

        await hold.ReleaseAsync();
        foreach (Guid order in orders)
        {
            await BffServiceFixture.WaitUntilAsync(async () =>
                (await fixture.OrderAsync(order)) is { DispatchedAt: not null });
        }
    }

    private async Task<Guid> PlacedAsync()
    {
        Guid order = Guid.CreateVersion7();
        await fixture.DeliverAsync(OrderEvents.Placed(order, Guid.CreateVersion7(), At));

        return order;
    }

    /// <summary>An update lock on each row, held by a transaction of the test's own until released.</summary>
    private sealed class RowHold : IAsyncDisposable
    {
        private readonly string _connectionString;
        private readonly SqlConnection _connection;
        private readonly SqlTransaction _transaction;
        private readonly short _session;

        private RowHold(string connectionString, SqlConnection connection, SqlTransaction transaction, short session)
        {
            _connectionString = connectionString;
            _connection = connection;
            _transaction = transaction;
            _session = session;
        }

        public static async Task<RowHold> TakeAsync(string connectionString, Guid[] orders)
        {
            SqlConnection connection = new(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            SqlTransaction transaction = connection.BeginTransaction();

            // UPDLOCK, which every MERGE's own read of the key waits on.
            int held = await connection.ExecuteScalarAsync<int>(
                """
                SELECT COUNT(*)
                FROM bff.Orders WITH (UPDLOCK, ROWLOCK)
                WHERE OrderId IN @Orders;
                """,
                new { Orders = orders },
                transaction);
            held.ShouldBe(orders.Length);

            short session = await connection.ExecuteScalarAsync<short>("SELECT @@SPID;", transaction: transaction);

            return new RowHold(connectionString, connection, transaction, session);
        }

        /// <summary>The requests waiting on a lock in this database, every one of them behind this hold.</summary>
        public async Task<int> BlockedAsync()
        {
            // A connection of its own, since an open one has already dropped its password from the string.
            await using SqlConnection observer = new(_connectionString);

            // Not those naming this session as blocker: a second request for one row queues behind the first,
            // and SQL Server names that first waiter instead.
            return await observer.ExecuteScalarAsync<int>(
                """
                SELECT COUNT(*)
                FROM sys.dm_exec_requests
                WHERE database_id = DB_ID()
                    AND wait_type LIKE N'LCK[_]M[_]%'
                    AND session_id <> @Session;
                """,
                new { Session = _session });
        }

        public Task ReleaseAsync() => _transaction.CommitAsync(TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            await _transaction.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
