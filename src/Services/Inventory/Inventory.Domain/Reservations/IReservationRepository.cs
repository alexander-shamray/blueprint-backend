namespace Inventory.Domain.Reservations;

public interface IReservationRepository
{
    /// <summary>
    /// Loads the reservation under a lock held to the end of the unit of work, on the row or on its absent key,
    /// so two creators serialise here instead of meeting on the key.
    /// </summary>
    Task<Reservation?> GetForUpdateAsync(OrderId id, CancellationToken ct);

    void Add(Reservation reservation);
}
