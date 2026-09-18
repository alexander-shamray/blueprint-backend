using Common.Domain;
using Inventory.Domain.Reservations.Events;
using Inventory.Domain.Stock;
using Inventory.Domain.Stock.Events;

namespace Inventory.Domain.Reservations;

/// <summary>
/// One row per order (§3.2). The one aggregate every message-driven command
/// modifies; the stock counters move underneath it by §7.3's statement. A
/// <see cref="ReservationStatus.Released"/> row with no lines is ADR-024's
/// tombstone.
/// </summary>
public sealed class Reservation : AggregateRoot<OrderId>
{
    private readonly List<ReservationLine> _lines = [];
    private readonly List<ProductId> _unavailable = [];

    public ReservationStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public IReadOnlyList<ReservationLine> Lines => _lines.AsReadOnly();
    public IReadOnlyList<ProductId> UnavailableProductIds => _unavailable.AsReadOnly();
    public DateTimeOffset? DespatchedUnreservedAt { get; private set; }

    private Reservation() { }

    private Reservation(OrderId id, ReservationStatus status, IEnumerable<ReservationLine> lines, DateTimeOffset now)
    {
        Id = id;
        Status = status;
        CreatedAt = now;
        UpdatedAt = now;
        _lines.AddRange(lines);
    }

    public static Reservation Reserve(
        OrderId order,
        IReadOnlyList<ReservationLine> lines,
        IReadOnlyList<ReservedLevel> levels,
        DateTimeOffset now)
    {
        CheckLines(lines);
        var reservation = new Reservation(order, ReservationStatus.Reserved, lines, now);
        reservation.Raise(new StockReservedDomainEvent(order, now));
        reservation.RaiseLevels(levels, now);
        return reservation;
    }

    public static Reservation Fail(
        OrderId order,
        IReadOnlyList<ReservationLine> lines,
        IReadOnlyList<ProductId> unavailable,
        DateTimeOffset now)
    {
        CheckLines(lines);
        var reservation = new Reservation(order, ReservationStatus.Failed, lines, now);
        reservation._unavailable.AddRange(unavailable);
        reservation.Raise(new StockReservationFailedDomainEvent(order, unavailable, now));
        return reservation;
    }

    public static Reservation Tombstone(OrderId order, DateTimeOffset now)
    {
        var reservation = new Reservation(order, ReservationStatus.Released, [], now);
        reservation.Raise(new StockReleasedDomainEvent(order, now));
        return reservation;
    }

    internal static Reservation Rehydrate(
        OrderId order,
        ReservationStatus status,
        IReadOnlyList<ReservationLine> lines,
        IReadOnlyList<ProductId>? unavailable = null)
    {
        var reservation = new Reservation(order, status, lines, DateTimeOffset.MinValue);
        reservation._unavailable.AddRange(unavailable ?? []);
        return reservation;
    }

    /// <summary>ADR-024's first guarantee: every call raises the postcondition.</summary>
    public void Release(IReadOnlyList<ReservedLevel> levels, DateTimeOffset now)
    {
        if (Status == ReservationStatus.Reserved)
        {
            Status = ReservationStatus.Released;
            UpdatedAt = now;
            RaiseLevels(levels, now);
        }

        Raise(new StockReleasedDomainEvent(Id, now));
    }

    /// <summary>The runbook's reinstatement: an operator's act, so no <c>StockReserved</c>.</summary>
    public void Reinstate(IReadOnlyList<ReservedLevel> levels, DateTimeOffset now)
    {
        if (Status != ReservationStatus.Released || _lines.Count == 0 || DespatchedUnreservedAt is not null)
        {
            throw new DomainException(
                "Only a released reservation with lines, not yet despatched, can be reinstated.");
        }

        Status = ReservationStatus.Reserved;
        UpdatedAt = now;
        RaiseLevels(levels, now);
    }

    /// <summary>Reaches <see cref="ReservationStatus.Fulfilled"/>; no §3.2 event describes despatch.</summary>
    public void Fulfil(DateTimeOffset now)
    {
        if (Status == ReservationStatus.Fulfilled)
            return;
        if (Status != ReservationStatus.Reserved)
            throw new DomainException("Only a held reservation can be fulfilled.");

        Status = ReservationStatus.Fulfilled;
        UpdatedAt = now;
    }

    /// <summary>ADR-029's open case: a despatch against a row already released, recorded as state.</summary>
    public void RecordDespatchUnreserved(DateTimeOffset now)
    {
        if (Status != ReservationStatus.Released || _lines.Count == 0)
            throw new DomainException("Only a released reservation with lines can record an unreserved despatch.");
        if (DespatchedUnreservedAt is not null)
            return;

        DespatchedUnreservedAt = now;
        UpdatedAt = now;
        Raise(new DespatchedUnreservedDomainEvent(Id, now));
    }

    /// <summary>A command that arrives again is answered again rather than ignored (ADR-024).</summary>
    public void AnswerAgain(DateTimeOffset now)
    {
        switch (Status)
        {
            case ReservationStatus.Reserved:
            case ReservationStatus.Fulfilled:
                Raise(new StockReservedDomainEvent(Id, now));
                break;
            case ReservationStatus.Failed:
                Raise(new StockReservationFailedDomainEvent(Id, UnavailableProductIds, now));
                break;
            case ReservationStatus.Released:
                Raise(new StockReleasedDomainEvent(Id, now));
                break;
            default:
                throw new DomainException($"No answer is defined for {Status}.");
        }
    }

    private void RaiseLevels(IReadOnlyList<ReservedLevel> levels, DateTimeOffset now)
    {
        // The row's own timestamp, not `now`: `now` orders the reservation's
        // events, the row's UpdatedAt orders the product's.
        foreach (ReservedLevel level in levels)
            Raise(new StockLevelChangedDomainEvent(level.ProductId, level.Available, level.UpdatedAt));
    }

    private static void CheckLines(IReadOnlyList<ReservationLine> lines)
    {
        if (lines.Count == 0)
            throw new DomainException("A reservation needs at least one line.");
        if (lines.Select(l => l.ProductId).Distinct().Count() != lines.Count)
            throw new DomainException("A product appears at most once in a reservation.");
        if (lines.Any(l => l.Quantity <= 0))
            throw new DomainException("A line's quantity must be positive.");
    }
}
