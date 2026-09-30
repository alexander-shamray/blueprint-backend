using Common.Application;
using Microsoft.EntityFrameworkCore;

namespace Common.Infrastructure.Idempotency;

/// <summary>§8.5's marker store over the service's own <c>DbContext</c>, inside the command's transaction.</summary>
/// <remarks>The <c>DbContext</c> is the service's alias, never a second context (ADR-037).</remarks>
public sealed class EfIdempotencyMarkerStore(DbContext db) : IIdempotencyMarkerStore
{
    public Task<bool> ExistsAsync(string key, CancellationToken ct) =>
        // A query, never the tracker: the question is what an earlier attempt committed, not what this one staged.
        db.Set<IdempotencyMarker>().AnyAsync(marker => marker.Key == key, ct);

    public async Task MarkAsync(string key, CancellationToken ct)
    {
        // Staged, not saved: §6.3 saves it in the same transaction as the aggregate.
        // No timestamp, so the column's default stamps it on the database's clock (ADR-038).
        await db.Set<IdempotencyMarker>().AddAsync(new IdempotencyMarker(key), ct);
    }
}
