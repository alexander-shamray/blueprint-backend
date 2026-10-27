using Common.Domain;
using Inventory.Domain.Reservations;
using Inventory.Domain.Stock;
using Shouldly;
using Xunit;

namespace Inventory.Domain.Tests;

/// <summary>The line guards' wording and edges, and what a rehydrated row holds as unavailable.</summary>
public class ReservationBoundaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly ProductId A = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"));
    private static readonly ProductId B = new(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"));

    private static Reservation Reserve(ReservationLine[] lines) =>
        Reservation.Reserve(OrderId.New(), lines, [], Now);

    private static Reservation Fail(ReservationLine[] lines) =>
        Reservation.Fail(OrderId.New(), lines, [A], Now);

    public static TheoryData<string> Creators => ["Reserve", "Fail"];

    private static Reservation Create(string name, ReservationLine[] lines) =>
        name == "Reserve" ? Reserve(lines) : Fail(lines);

    [Theory]
    [MemberData(nameof(Creators))]
    public void No_lines_is_refused_with_its_wording(string name)
    {
        DomainException thrown = Should.Throw<DomainException>(() => Create(name, []));

        thrown.Message.ShouldBe("A reservation needs at least one line.");
    }

    [Theory]
    [MemberData(nameof(Creators))]
    public void A_product_twice_is_refused_with_its_wording(string name)
    {
        DomainException thrown = Should.Throw<DomainException>(() => Create(name, [new(A, 1), new(A, 2)]));

        thrown.Message.ShouldBe("A product appears at most once in a reservation.");
    }

    [Theory]
    [MemberData(nameof(Creators))]
    public void A_zero_quantity_is_refused_with_its_wording(string name)
    {
        DomainException thrown = Should.Throw<DomainException>(() => Create(name, [new(A, 0)]));

        thrown.Message.ShouldBe("A line's quantity must be positive.");
    }

    [Theory]
    [MemberData(nameof(Creators))]
    public void One_bad_line_among_good_ones_is_refused_wherever_it_stands(string name)
    {
        Should.Throw<DomainException>(() => Create(name, [new(A, 1), new(B, 0)]));
        Should.Throw<DomainException>(() => Create(name, [new(A, 0), new(B, 1)]));
    }

    [Theory]
    [MemberData(nameof(Creators))]
    public void A_negative_quantity_is_refused(string name)
    {
        Should.Throw<DomainException>(() => Create(name, [new(A, -1)]));
    }

    [Theory]
    [MemberData(nameof(Creators))]
    public void A_quantity_of_one_is_accepted(string name)
    {
        Create(name, [new(A, 1)]).Lines.ShouldBe([new(A, 1)]);
    }

    [Fact]
    public void Fail_keeps_what_was_short_on_the_row()
    {
        Reservation reservation = Reservation.Fail(OrderId.New(), [new(A, 1), new(B, 1)], [B], Now);

        reservation.UnavailableProductIds.ShouldBe([B]);
    }

    [Fact]
    public void A_rehydrated_row_holds_the_unavailable_products_it_is_given()
    {
        Reservation reservation = Reservation.Rehydrate(
            OrderId.New(),
            ReservationStatus.Failed,
            [new(A, 1), new(B, 1)],
            [A, B]);

        reservation.UnavailableProductIds.ShouldBe([A, B]);
    }

    [Fact]
    public void A_rehydrated_row_given_none_holds_none()
    {
        Reservation.Rehydrate(OrderId.New(), ReservationStatus.Reserved, [new(A, 1)])
            .UnavailableProductIds.ShouldBeEmpty();
    }
}
