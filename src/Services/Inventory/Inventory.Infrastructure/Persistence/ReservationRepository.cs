using Inventory.Domain.Reservations;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

internal sealed class ReservationRepository(InventoryDbContext db) : IReservationRepository
{
    public async Task<Reservation?> GetForUpdateAsync(OrderId id, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "A reservation is read only inside the unit of work's transaction (§6.3).");
        }

        // The lock probe runs first and on its own: UPDLOCK on the row, or a
        // key-range lock on its absence, held until the transaction ends. EF's
        // query below then reads what the probe locked.
        await db.Database.ExecuteSqlAsync(
            $"SELECT OrderId FROM inventory.Reservations WITH (UPDLOCK, HOLDLOCK) WHERE OrderId = {id.Value};",
            ct);

        return await db.Reservations.SingleOrDefaultAsync(r => r.Id == id, ct);
    }

    public void Add(Reservation reservation) => db.Add(reservation);
}
