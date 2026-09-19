using Inventory.Application.Reservations;
using Inventory.Application.Reservations.Fulfil;
using Inventory.Application.Reservations.ReleaseStock;
using Inventory.Application.Reservations.ReserveStock;
using Inventory.Domain.Reservations;
using Inventory.Domain.Reservations.Events;
using Inventory.Domain.Stock;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Inventory.Application.Tests;

/// <summary>
/// Every handler that stamps a reservation reads
/// <see cref="TimeProvider.GetUtcNow"/> after <c>GetForUpdateAsync</c> returns,
/// so a writer that waited behind the lock cannot stamp earlier than the one it
/// waited for. <see cref="LockAdvancingClock"/>
/// stands in for the wait: it moves the clock forward inside the fake
/// repository's call, so a handler that samples the clock first would still
/// observe the earlier instant.
/// </summary>
public sealed class ReservationClockOrderingTests
{
    private static readonly DateTimeOffset BeforeLock = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan WaitBehindLock = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ReleaseStock_stamps_the_instant_taken_after_the_lock()
    {
        var clock = new LockAdvancingClock(BeforeLock, WaitBehindLock);
        var order = OrderId.New();
        var reserved = Reservation.Reserve(order, [new ReservationLine(ProductId.New(), 1)], [], BeforeLock);
        var repository = new FakeReservationRepository(reserved, clock);
        var handler = new ReleaseStockHandler(repository, new FakeStockLedger(), clock);

        await handler.HandleAsync(new ReleaseStockCommand(order.Value, CommandOrigin.User), CancellationToken.None);

        StockReleasedDomainEvent released = reserved.DomainEvents.OfType<StockReleasedDomainEvent>().Last();
        released.OccurredAt.ShouldBe(BeforeLock + WaitBehindLock);
    }

    [Fact]
    public async Task ReserveStock_stamps_the_instant_taken_after_the_lock()
    {
        var clock = new LockAdvancingClock(BeforeLock, WaitBehindLock);
        var order = OrderId.New();
        var existing = Reservation.Reserve(order, [new ReservationLine(ProductId.New(), 1)], [], BeforeLock);
        var repository = new FakeReservationRepository(existing, clock);
        var handler = new ReserveStockHandler(repository, new FakeStockLedger(), clock);

        await handler.HandleAsync(
            new ReserveStockCommand(order.Value, [new ReservationLine(ProductId.New(), 1)]),
            CancellationToken.None);

        StockReservedDomainEvent answered = existing.DomainEvents.OfType<StockReservedDomainEvent>().Last();
        answered.OccurredAt.ShouldBe(BeforeLock + WaitBehindLock);
    }

    [Fact]
    public async Task Fulfilment_stamps_the_instant_taken_after_the_lock()
    {
        var clock = new LockAdvancingClock(BeforeLock, WaitBehindLock);
        var order = OrderId.New();
        Reservation held = Reservation.Reserve(order, [new ReservationLine(ProductId.New(), 1)], [], BeforeLock);
        var repository = new FakeReservationRepository(held, clock);
        var handler = new FulfilReservationHandler(
            repository, new FakeStockLedger(), clock, NullLogger<FulfilReservationHandler>.Instance);

        await handler.HandleAsync(new FulfilReservationCommand(order.Value), CancellationToken.None);

        held.UpdatedAt.ShouldBe(BeforeLock + WaitBehindLock);
    }

    [Fact]
    public async Task An_unreserved_despatch_stamps_the_instant_taken_after_the_lock()
    {
        var clock = new LockAdvancingClock(BeforeLock, WaitBehindLock);
        var order = OrderId.New();
        Reservation released = Reservation.Reserve(order, [new ReservationLine(ProductId.New(), 1)], [], BeforeLock);
        released.Release([], BeforeLock);
        var repository = new FakeReservationRepository(released, clock);
        var handler = new FulfilReservationHandler(
            repository, new FakeStockLedger(), clock, NullLogger<FulfilReservationHandler>.Instance);

        await handler.HandleAsync(new FulfilReservationCommand(order.Value), CancellationToken.None);

        released.DespatchedUnreservedAt.ShouldBe(BeforeLock + WaitBehindLock);
    }

    /// <summary>Advances by <c>wait</c> on every call, standing in for a writer queued behind the lock.</summary>
    private sealed class LockAdvancingClock(DateTimeOffset start, TimeSpan wait) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void AdvanceAsIfWaitedForTheLock() => _now += wait;
    }

    private sealed class FakeReservationRepository(Reservation? existing, LockAdvancingClock clock)
        : IReservationRepository
    {
        public Task<Reservation?> GetForUpdateAsync(OrderId id, CancellationToken ct)
        {
            clock.AdvanceAsIfWaitedForTheLock();
            return Task.FromResult(existing);
        }

        public void Add(Reservation reservation)
        {
        }
    }

    private sealed class FakeStockLedger : IStockLedger
    {
        public Task<LedgerOutcome> TryTakeAsync(IReadOnlyList<ReservationLine> lines, CancellationToken ct) =>
            Task.FromResult(new LedgerOutcome([], []));

        public Task<IReadOnlyList<ReservedLevel>> GiveBackAsync(
            IReadOnlyList<ReservationLine> lines, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ReservedLevel>>([]);

        public Task FulfilAsync(IReadOnlyList<ReservationLine> lines, CancellationToken ct) => Task.CompletedTask;
    }
}
