using System.Data.Common;
using Common.Application;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Application.Orders.PlaceOrder;
using Ordering.Application.Privacy;
using Ordering.Application.Privacy.ErasePersonalData;
using Ordering.Domain.Common;
using Ordering.Domain.Orders;
using Ordering.Domain.Orders.Events;
using Ordering.Infrastructure.Persistence;
using Ordering.Infrastructure.Projections;
using Ordering.TestSupport;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>An erasure misses nothing a placement or its projection writes while it runs (ADR-137).</summary>
/// <remarks>
/// Each side runs in its own <see cref="EfUnitOfWork"/> with a pause on one insert, so the other starts while the first
/// holds its lock.
/// </remarks>
[Collection(nameof(IntegrationCollection))]
public sealed class OrderingErasureFenceTests(ServiceFixture fixture) : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private static readonly Guid Subject = new("dddddddd-dddd-dddd-dddd-dddddddddddd");

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_placement_waits_for_an_erasure_that_found_no_order_and_is_then_placed_under_it()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Guid product = await SeedPriceAsync();
        Guid request = Guid.CreateVersion7();
        PauseBefore pause = new("INSERT INTO [ordering].[PersonalDataErasures]");

        Task<Result<int>> erasure = EraseAsync(request, pause, ct);
        (await pause.Reached.Task.WaitAsync(Deadline, ct))
            .ShouldBeTrue("the erasure has run its statements and not yet written its audit row");

        Task<Result<Guid>> placed = PlaceAsync(product, pause: null, ct);

        await Should.ThrowAsync<TimeoutException>(() => placed.WaitAsync(Wait, ct));

        pause.Release.SetResult();
        await erasure.WaitAsync(Deadline, ct);
        Guid order = (await placed.WaitAsync(Deadline, ct)).Value;

        (await fixture.ScalarAsync<string>(
            "SELECT Value = ErasedUnder FROM ordering.Orders WHERE Id = {0} AND CustomerId = {1}",
            order,
            Subject)).ShouldBe($"[\"{request}\"]", "the placement waited, then read the audit row and stamped it");
    }

    [Fact]
    public async Task An_erasure_waits_for_a_placement_that_read_no_request_and_then_erases_its_order()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Guid product = await SeedPriceAsync();
        PauseBefore pause = new("INSERT INTO [ordering].[Orders]");

        Task<Result<Guid>> placed = PlaceAsync(product, pause, ct);
        (await pause.Reached.Task.WaitAsync(Deadline, ct))
            .ShouldBeTrue("the placement has read the erasures and not yet inserted its order");

        Task<Result<int>> erasure = EraseAsync(Guid.CreateVersion7(), pause: null, ct);

        await Should.ThrowAsync<TimeoutException>(() => erasure.WaitAsync(Wait, ct));

        pause.Release.SetResult();
        Guid order = (await placed.WaitAsync(Deadline, ct)).Value;
        (await erasure.WaitAsync(Deadline, ct)).Value.ShouldBe(1, "the erasure waited, then found the order");

        (await fixture.ScalarAsync<Guid>(
            "SELECT Value = CustomerId FROM ordering.Orders WHERE Id = {0}",
            order)).ShouldBe(Guid.Empty);
    }

    [Fact]
    public async Task An_erasure_waits_for_a_placement_being_projected_and_then_takes_the_buyer_off_its_summary()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Guid order = await fixture.SeedOrderAsync(Subject);

        // Holds the summary's key, so the projection stops at its insert with the order already read.
        await using SqlConnection blocker = new(fixture.ConnectionString);
        await blocker.OpenAsync(ct);
        await using DbTransaction held = await blocker.BeginTransactionAsync(ct);
        int session = await blocker.ExecuteScalarAsync<int>(
            new CommandDefinition("SELECT @@SPID", transaction: held, cancellationToken: ct));
        await blocker.ExecuteAsync(
            new CommandDefinition(
                "SELECT OrderId FROM ordering.OrderSummaries WITH (UPDLOCK, HOLDLOCK) WHERE OrderId = @order",
                new { order },
                held,
                cancellationToken: ct));

        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        Task projected = scope.ServiceProvider
            .GetServices<IProjectionHandler<OrderPlacedDomainEvent>>()
            .OfType<OrderSummaryProjection>()
            .Single()
            .HandleAsync(
                new OrderPlacedDomainEvent(
                    new OrderId(order),
                    new CustomerId(Subject),
                    Money.Of(19.99m, "EUR"),
                    [new OrderLineSnapshot(ProductId.New(), 1, Money.Of(19.99m, "EUR"))],
                    [],
                    DateTimeOffset.UtcNow),
                ct);
        await BlockedByAsync(session, ct);

        Task<Result<int>> erasure = EraseAsync(Guid.CreateVersion7(), pause: null, ct);

        await Should.ThrowAsync<TimeoutException>(() => erasure.WaitAsync(Wait, ct));

        await held.CommitAsync(ct);
        await projected.WaitAsync(Deadline, ct);
        (await erasure.WaitAsync(Deadline, ct)).Value.ShouldBe(2, "the erasure waited, then found the summary");

        (await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM ordering.OrderSummaries WHERE OrderId = {0} AND CustomerId IS NULL " +
            "AND PlacedAt IS NOT NULL",
            order)).ShouldBe(1);
    }

    [Fact]
    public async Task A_lock_held_past_the_wait_is_a_typed_fault_and_not_the_engines()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        await using SqlConnection holder = new(fixture.ConnectionString);
        await holder.OpenAsync(ct);
        await using DbTransaction held = await holder.BeginTransactionAsync(ct);
        await holder.ExecuteAsync(
            new CommandDefinition(
                "EXEC sp_getapplock @Resource = @r, @LockMode = 'Exclusive', @LockOwner = 'Transaction', " +
                "@LockTimeout = 0",
                new { r = $"ordering.erasure.subject.{Subject:N}" },
                held,
                cancellationToken: ct));

        // Waits out the lock's ten seconds, which is the cost of proving the timeout rather than a stand-in for it.
        CustomerLockNotTakenException thrown = await Should.ThrowAsync<CustomerLockNotTakenException>(
            () => EraseAsync(Guid.CreateVersion7(), pause: null, ct));

        thrown.InnerException.ShouldBeOfType<SqlException>().Number
            .ShouldBe(PersonalDataErasureRepository.LockNotTakenNumber);
    }

    /// <summary>Returns once a request is waiting on the given session, read from the server's own view.</summary>
    private async Task BlockedByAsync(int session, CancellationToken ct)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + Deadline;

        while (await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id = {0}",
            session) == 0)
        {
            DateTimeOffset.UtcNow.ShouldBeLessThan(deadline, "the projection never reached the held summary");
            await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
        }
    }

    private Task<Result<Guid>> PlaceAsync(Guid product, PauseBefore? pause, CancellationToken ct) =>
        InUnitAsync(
            pause,
            (db, token) => new PlaceOrderHandler(
                    new OrderRepository(db),
                    new ProjectedPriceReader(fixture.Factory.Services.GetRequiredService<IDbConnectionFactory>()),
                    new PersonalDataErasureRepository(db),
                    new Caller(),
                    TimeProvider.System)
                .HandleAsync(
                    new PlaceOrderCommand(
                        Guid.CreateVersion7(),
                        [new PlaceOrderItem(product, 1)],
                        new AddressDto("1 Test Street", null, "Almaty", "050000", "KZ"),
                        "EUR"),
                    token),
            ct);

    private Task<Result<int>> EraseAsync(Guid request, PauseBefore? pause, CancellationToken ct) =>
        InUnitAsync(
            pause,
            (db, token) => new ErasePersonalDataHandler(
                    new SqlOrderPersonalDataStore(db),
                    new PersonalDataErasureRepository(db),
                    TimeProvider.System)
                .HandleAsync(new ErasePersonalDataCommand(request, Subject), token),
            ct);

    /// <summary>§6.3's unit as the transaction behaviour runs it, on a context of the test's own.</summary>
    private async Task<TResult> InUnitAsync<TResult>(
        PauseBefore? pause,
        Func<OrderingDbContext, CancellationToken, Task<TResult>> work,
        CancellationToken ct)
    {
        DbContextOptionsBuilder<OrderingDbContext> options =
            new DbContextOptionsBuilder<OrderingDbContext>().UseSqlServer(fixture.ConnectionString);

        if (pause is not null)
            options.AddInterceptors(pause);

        await using OrderingDbContext db = new(options.Options);
        EfUnitOfWork unit = new(db);

        return await unit.ExecuteAsync(
            async token =>
            {
                TResult result = await work(db, token);
                await unit.SaveChangesAsync(token);
                return result;
            },
            ct);
    }

    private async Task<Guid> SeedPriceAsync()
    {
        Guid product = Guid.CreateVersion7();
        await fixture.ExecuteAsync(
            "INSERT INTO ordering.ProductPrices (ProductId, Currency, Amount, IsAvailable, UpdatedAt) " +
            "VALUES ({0}, 'EUR', 19.99, 1, SYSDATETIMEOFFSET())",
            product);

        return product;
    }

    private sealed class Caller : ICurrentUser
    {
        public bool IsAuthenticated => true;

        public Guid Id => Subject;

        public bool HasPermission(string permission) => true;
    }

    /// <summary>Holds a unit at the first command whose text holds the given insert, until released.</summary>
    private sealed class PauseBefore(string insert) : DbCommandInterceptor
    {
        public TaskCompletionSource<bool> Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(insert, StringComparison.Ordinal))
            {
                Reached.TrySetResult(true);
                await Release.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }
}
