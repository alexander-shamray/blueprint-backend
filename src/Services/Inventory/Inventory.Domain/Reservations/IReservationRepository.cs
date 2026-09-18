namespace Inventory.Domain.Reservations;

public interface IReservationRepository
{
    /// <summary>
    /// Loads the reservation under a lock held to the end of the unit of
    /// work: the row where one exists, the key range where none does. Two
    /// creators serialise here instead of meeting on the key, and a reply
    /// derived from the row's state runs against a row nobody else can
    /// change until this commits (spec, section 7).
    /// </summary>
    Task<Reservation?> GetForUpdateAsync(OrderId id, CancellationToken ct);

    void Add(Reservation reservation);
}
