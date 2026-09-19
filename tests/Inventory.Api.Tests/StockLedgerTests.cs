using Inventory.Application.Reservations;
using Inventory.Domain.Reservations;
using Inventory.Domain.Stock;
using Inventory.Infrastructure.Persistence;
using Inventory.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Inventory.Api.Tests;

[Collection(nameof(IntegrationCollection))]
public sealed class StockLedgerTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<T> InTransaction<T>(Func<IStockLedger, Task<T>> act, bool commit = true)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        InventoryDbContext db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        IStockLedger ledger = scope.ServiceProvider.GetRequiredService<IStockLedger>();
        await using IDbContextTransaction tx =
            await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        T result = await act(ledger);
        if (commit)
            await tx.CommitAsync(TestContext.Current.CancellationToken);
        return result;
    }

    private Task Seed(Guid product, int available, int reserved = 0) =>
        fixture.ExecuteAsync(
            "INSERT INTO inventory.StockItems (ProductId, Available, Reserved, UpdatedAt) " +
            "VALUES ({0}, {1}, {2}, SYSDATETIMEOFFSET())",
            product, available, reserved);

    private Task<int> Available(Guid product) =>
        fixture.ScalarAsync<int>("SELECT Value = Available FROM inventory.StockItems WHERE ProductId = {0}", product);

    [Fact]
    public async Task Taking_every_line_decrements_and_returns_the_levels_left()
    {
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();
        await Seed(a, 5);
        await Seed(b, 1);

        LedgerOutcome outcome = await InTransaction(l =>
            l.TryTakeAsync(
                [new(new ProductId(b), 1), new(new ProductId(a), 2)],
                TestContext.Current.CancellationToken));

        outcome.Unavailable.ShouldBeEmpty();
        outcome.Levels.Select(l => (l.ProductId, l.Available))
            .ShouldBe([(new ProductId(a), 3), (new ProductId(b), 0)], ignoreOrder: true);
        outcome.Levels.ShouldAllBe(l => l.UpdatedAt > DateTimeOffset.UtcNow.AddMinutes(-1),
            "the instant is the statement's, stamped under the row lock");
        // Version-7 ids are not creation-ordered under Guid.CompareTo, so the
        // order is asserted against the comparer the ledger sorts with, not
        // against which id was made first.
        outcome.Levels.Select(l => l.ProductId.Value)
            .ShouldBe(
                outcome.Levels.Select(l => l.ProductId.Value).OrderBy(g => g),
                "in ProductId order, whatever order the lines came in");
        (await Available(a)).ShouldBe(3);
        (await Available(b)).ShouldBe(0);
    }

    [Fact]
    public async Task A_row_stamped_in_the_future_is_stamped_one_tick_later_and_never_earlier()
    {
        var a = Guid.CreateVersion7();
        DateTimeOffset future = DateTimeOffset.UtcNow.AddHours(1);
        await fixture.ExecuteAsync(
            "INSERT INTO inventory.StockItems (ProductId, Available, Reserved, UpdatedAt) " +
            "VALUES ({0}, 5, 0, {1})",
            a,
            future);

        LedgerOutcome outcome = await InTransaction(l =>
            l.TryTakeAsync([new(new ProductId(a), 1)], TestContext.Current.CancellationToken));

        outcome.Levels.ShouldHaveSingleItem().UpdatedAt.ShouldBe(future.AddTicks(1),
            "per-product monotonic: a clock behind the row's stamp does not move the stamp backwards");
        (await Available(a)).ShouldBe(4);
    }

    [Fact]
    public async Task A_short_line_rolls_every_decrement_back_and_names_every_short_product()
    {
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();
        var c = Guid.CreateVersion7();
        await Seed(a, 5);
        await Seed(b, 0);

        LedgerOutcome outcome = await InTransaction(l =>
            l.TryTakeAsync(
                [new(new ProductId(a), 2), new(new ProductId(b), 1), new(new ProductId(c), 1)],
                TestContext.Current.CancellationToken));

        outcome.Unavailable.Select(p => p.Value).ShouldBe(new[] { b, c }, ignoreOrder: true);
        outcome.Levels.ShouldBeEmpty();
        (await Available(a)).ShouldBe(5, "the savepoint undid the first line's decrement");
    }

    [Fact]
    public async Task Two_takes_for_the_last_unit_leave_exactly_one_holding_it()
    {
        var a = Guid.CreateVersion7();
        await Seed(a, 1);
        ReservationLine[] line = [new(new ProductId(a), 1)];

        Task<LedgerOutcome> first = InTransaction(l => l.TryTakeAsync(line, TestContext.Current.CancellationToken));
        Task<LedgerOutcome> second = InTransaction(l => l.TryTakeAsync(line, TestContext.Current.CancellationToken));
        LedgerOutcome[] outcomes = await Task.WhenAll(first, second);

        outcomes.Count(o => o.Unavailable.Count == 0).ShouldBe(1, "§7.3's whole argument");
        (await Available(a)).ShouldBe(0);
    }

    [Fact]
    public async Task Giving_back_adds_to_available_with_no_guard_and_reports_the_levels()
    {
        var a = Guid.CreateVersion7();
        await Seed(a, 0, reserved: 2);

        IReadOnlyList<ReservedLevel> levels = await InTransaction(l =>
            l.GiveBackAsync([new(new ProductId(a), 2)], TestContext.Current.CancellationToken));

        levels.ShouldHaveSingleItem().Available.ShouldBe(2);
        (await fixture.ScalarAsync<int>("SELECT Value = Reserved FROM inventory.StockItems WHERE ProductId = {0}", a))
            .ShouldBe(0);
    }

    [Fact]
    public async Task Giving_back_a_line_whose_row_is_gone_is_a_fault_not_a_release()
    {
        InvalidOperationException error = await Should.ThrowAsync<InvalidOperationException>(() => InTransaction(l =>
            l.GiveBackAsync([new(ProductId.New(), 1)], TestContext.Current.CancellationToken)));

        // Pinned so an unrelated EF InvalidOperationException under
        // EnableRetryOnFailure cannot satisfy this by type alone.
        error.Message.ShouldContain("held line and no stock row");
    }

    [Fact]
    public async Task The_ledger_refuses_to_run_outside_a_transaction()
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IStockLedger ledger = scope.ServiceProvider.GetRequiredService<IStockLedger>();

        InvalidOperationException error = await Should.ThrowAsync<InvalidOperationException>(() =>
            ledger.TryTakeAsync([new(ProductId.New(), 1)], TestContext.Current.CancellationToken));

        // Pinned for the same reason as above.
        error.Message.ShouldContain("inside the unit of work's transaction");
    }
}
