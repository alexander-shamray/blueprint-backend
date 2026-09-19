using Common.Domain;
using Inventory.Domain.Reservations;
using Inventory.Domain.Reservations.Events;
using Inventory.Domain.Stock;
using Inventory.Domain.Stock.Events;
using Shouldly;
using Xunit;

namespace Inventory.Domain.Tests;

public class ReservationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly ProductId A = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"));
    private static readonly ProductId B = new(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"));

    private static IReadOnlyList<ReservationLine> Lines() => [new(A, 2), new(B, 1)];

    private static IReadOnlyList<ReservedLevel> Levels() => [new(A, 8, Now), new(B, 0, Now.AddTicks(1))];

    [Fact]
    public void Reserve_holds_the_lines_and_raises_the_reservation_and_every_level()
    {
        OrderId order = OrderId.New();

        Reservation reservation = Reservation.Reserve(order, Lines(), Levels(), Now);

        reservation.Status.ShouldBe(ReservationStatus.Reserved);
        reservation.Lines.Count.ShouldBe(2);
        reservation.DomainEvents.OfType<StockReservedDomainEvent>().ShouldHaveSingleItem().OrderId.ShouldBe(order);
        reservation.DomainEvents.OfType<StockLevelChangedDomainEvent>()
            .Select(e => (e.ProductId, e.Available, e.OccurredAt))
            .ShouldBe(
                [(A, 8, Now), (B, 0, Now.AddTicks(1))],
                "each level carries the row's instant, not the command's");
    }

    [Fact]
    public void Fail_records_the_lines_and_names_what_was_short()
    {
        Reservation reservation = Reservation.Fail(OrderId.New(), Lines(), [B], Now);

        reservation.Status.ShouldBe(ReservationStatus.Failed);
        reservation.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<StockReservationFailedDomainEvent>()
            .UnavailableProductIds.ShouldBe([B]);
    }

    [Fact]
    public void A_tombstone_is_released_with_no_lines_and_publishes_the_postcondition()
    {
        Reservation reservation = Reservation.Tombstone(OrderId.New(), Now);

        reservation.Status.ShouldBe(ReservationStatus.Released);
        reservation.Lines.ShouldBeEmpty();
        reservation.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<StockReleasedDomainEvent>();
    }

    [Fact]
    public void Release_of_a_held_reservation_frees_the_lines_and_reports_the_levels()
    {
        Reservation reservation = Reservation.Reserve(OrderId.New(), Lines(), Levels(), Now);
        reservation.ClearDomainEvents();

        reservation.Release([new(A, 10, Now), new(B, 1, Now)], Now);

        reservation.Status.ShouldBe(ReservationStatus.Released);
        reservation.Lines.Count.ShouldBe(2, "the lines are kept for a later reinstatement");
        reservation.DomainEvents.OfType<StockReleasedDomainEvent>().ShouldHaveSingleItem();
        reservation.DomainEvents.OfType<StockLevelChangedDomainEvent>().Count().ShouldBe(2);
    }

    [Theory]
    [InlineData(ReservationStatus.Failed)]
    [InlineData(ReservationStatus.Released)]
    [InlineData(ReservationStatus.Fulfilled)]
    public void Release_of_anything_not_held_publishes_the_postcondition_and_moves_nothing(ReservationStatus status)
    {
        Reservation reservation = Reservation.Rehydrate(OrderId.New(), status, Lines());

        reservation.Release([], Now);

        reservation.Status.ShouldBe(status);
        reservation.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<StockReleasedDomainEvent>();
    }

    [Fact]
    public void Reinstate_restores_a_released_reservation_and_publishes_only_the_levels()
    {
        Reservation reservation = Reservation.Rehydrate(OrderId.New(), ReservationStatus.Released, Lines());

        reservation.Reinstate(Levels(), Now);

        reservation.Status.ShouldBe(ReservationStatus.Reserved);
        reservation.DomainEvents.ShouldAllBe(e => e is StockLevelChangedDomainEvent);
        reservation.DomainEvents.Count.ShouldBe(2);
    }

    [Fact]
    public void Reinstate_refuses_a_tombstone_and_anything_not_released()
    {
        Should.Throw<DomainException>(() => Reservation.Tombstone(OrderId.New(), Now).Reinstate([], Now));
        Should.Throw<DomainException>(() =>
            Reservation.Rehydrate(OrderId.New(), ReservationStatus.Reserved, Lines()).Reinstate(Levels(), Now));
    }

    [Theory]
    [InlineData(ReservationStatus.Reserved, typeof(StockReservedDomainEvent))]
    [InlineData(ReservationStatus.Fulfilled, typeof(StockReservedDomainEvent))]
    [InlineData(ReservationStatus.Failed, typeof(StockReservationFailedDomainEvent))]
    [InlineData(ReservationStatus.Released, typeof(StockReleasedDomainEvent))]
    public void AnswerAgain_re_raises_the_event_this_state_answers_with(ReservationStatus status, Type expected)
    {
        Reservation reservation = Reservation.Rehydrate(OrderId.New(), status, Lines(), unavailable: [B]);

        reservation.AnswerAgain(Now);

        reservation.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType(expected);
    }

    [Fact]
    public void Reserve_refuses_no_lines_and_a_repeated_product()
    {
        Should.Throw<DomainException>(() => Reservation.Reserve(OrderId.New(), [], [], Now));
        Should.Throw<DomainException>(() =>
            Reservation.Reserve(OrderId.New(), [new(A, 1), new(A, 1)], [new(A, 1, Now)], Now));
    }

    [Fact]
    public void Fulfil_moves_a_held_reservation_to_fulfilled_and_raises_nothing()
    {
        Reservation reservation = Reservation.Rehydrate(OrderId.New(), ReservationStatus.Reserved, Lines());

        reservation.Fulfil(Now);

        reservation.Status.ShouldBe(ReservationStatus.Fulfilled);
        reservation.DomainEvents.ShouldBeEmpty("no §3.2 event describes despatch and the level did not move");
    }

    [Fact]
    public void Fulfil_twice_is_a_no_op()
    {
        Reservation reservation = Reservation.Rehydrate(OrderId.New(), ReservationStatus.Fulfilled, Lines());

        reservation.Fulfil(Now);

        reservation.Status.ShouldBe(ReservationStatus.Fulfilled);
    }

    [Theory]
    [InlineData(ReservationStatus.Failed)]
    [InlineData(ReservationStatus.Released)]
    public void Fulfil_refuses_what_was_never_held_or_is_no_longer_held(ReservationStatus status)
    {
        Should.Throw<DomainException>(() => Reservation.Rehydrate(OrderId.New(), status, Lines()).Fulfil(Now));
    }

    [Fact]
    public void A_despatch_against_a_released_reservation_is_recorded_once_as_state()
    {
        Reservation reservation = Reservation.Rehydrate(OrderId.New(), ReservationStatus.Released, Lines());

        reservation.RecordDespatchUnreserved(Now);
        reservation.RecordDespatchUnreserved(Now.AddMinutes(1));

        reservation.Status.ShouldBe(ReservationStatus.Released, "no stock moves: ADR-029's gap stays open");
        reservation.DespatchedUnreservedAt.ShouldBe(Now);
        reservation.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<DespatchedUnreservedDomainEvent>();
    }

    [Fact]
    public void A_tombstone_cannot_record_a_despatch()
    {
        Should.Throw<DomainException>(() => Reservation.Tombstone(OrderId.New(), Now).RecordDespatchUnreserved(Now));
    }

    [Fact]
    public void A_reservation_whose_parcel_has_gone_cannot_be_reinstated()
    {
        Reservation reservation = Reservation.Rehydrate(OrderId.New(), ReservationStatus.Released, Lines());
        reservation.RecordDespatchUnreserved(Now);

        Should.Throw<DomainException>(() => reservation.Reinstate(Levels(), Now));
    }
}
