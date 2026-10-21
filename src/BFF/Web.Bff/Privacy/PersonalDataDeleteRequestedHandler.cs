using Common.Application;
using Common.Contracts.Privacy.V1;
using Common.Domain;
using Microsoft.EntityFrameworkCore;
using Web.Bff.Persistence;

namespace Web.Bff.Privacy;

/// <summary>Deletes the subject's orders, lines and saved address, audits it, then tells Privacy (ADR-092).</summary>
/// <remarks>
/// Public, because §6.2's scan is public-only. The report follows the commit, since a send inside the
/// execution strategy's retry would repeat (ADR-094); a request already seen repeats rather than fails (§11.7).
/// </remarks>
public sealed class PersonalDataDeleteRequestedHandler(
    BffDbContext db,
    IErasureReporter reporter,
    TimeProvider clock)
    : IIntegrationEventHandler<PersonalDataDeleteRequested>
{
    public async Task HandleAsync(PersonalDataDeleteRequested integrationEvent, CancellationToken ct)
    {
        // A closed request is not reissued, so this is a late copy; the checks no longer ask its row, and a second
        // pass would delete what the customer made since (ADR-139).
        DateTimeOffset? closedAt = await db.PersonalDataErasures
            .AsNoTracking()
            .Where(e => e.Id == integrationEvent.RequestId)
            .Select(e => e.ClosedAt)
            .FirstOrDefaultAsync(ct);
        if (closedAt is not null)
            return;

        // As the column's JSON spells a Guid, which is what an order's list is matched against.
        string requestId = integrationEvent.RequestId.ToString();

        int erased = await db.Database.CreateExecutionStrategy().ExecuteAsync(
            async () =>
            {
                // A re-run starts from nothing, so an audit row the failed attempt tracked is not added twice.
                db.ChangeTracker.Clear();

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                // The ids are kept first, so a shipment or payment event that names no customer cannot give the
                // deleted order a new row (ADR-098). HOLDLOCK takes a key-range lock even where the subject has no
                // order, and keeps it to the commit that writes the audit row: a projection write meanwhile waits,
                // and its erased check then sees that row. An order listing this request was placed after it, so it
                // stays, as the owner's own store keeps it (ADR-099).
                DateTimeOffset erasedAt = clock.GetUtcNow();
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO bff.ErasedOrders (OrderId, ErasedAt)
                    SELECT o.OrderId, {erasedAt}
                    FROM bff.Orders AS o WITH (UPDLOCK, HOLDLOCK)
                    WHERE o.CustomerId = {integrationEvent.SubjectId}
                        AND NOT EXISTS (SELECT 1 FROM OPENJSON(o.ErasedUnder) AS u WHERE u.value = {requestId})
                        AND NOT EXISTS (SELECT 1 FROM bff.ErasedOrders AS e WHERE e.OrderId = o.OrderId)
                    """,
                    ct);

                // The lines go with their order, by the foreign key's cascade (ADR-051).
                int deleted = await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    DELETE o FROM bff.Orders AS o
                    WHERE o.CustomerId = {integrationEvent.SubjectId}
                        AND NOT EXISTS (SELECT 1 FROM OPENJSON(o.ErasedUnder) AS u WHERE u.value = {requestId})
                    """,
                    ct);

                // The saved address is counted with the orders (ADR-107), and one saved under this request stays.
                // HOLDLOCK keeps the key to the commit where there is no row, so a save meanwhile reads the audit
                // row and stamps it (ADR-135).
                deleted += await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    DELETE a FROM bff.SavedAddresses AS a WITH (HOLDLOCK)
                    WHERE a.CustomerId = {integrationEvent.SubjectId}
                        AND NOT EXISTS (SELECT 1 FROM OPENJSON(a.ErasedUnder) AS u WHERE u.value = {requestId})
                    """,
                    ct);

                PersonalDataErasure? seen = await db.PersonalDataErasures
                    .FirstOrDefaultAsync(e => e.Id == integrationEvent.RequestId, ct);

                if (seen is null)
                {
                    seen = PersonalDataErasure.Record(
                        integrationEvent.RequestId,
                        integrationEvent.SubjectId,
                        deleted,
                        erasedAt);
                    db.PersonalDataErasures.Add(seen);
                }
                else
                {
                    seen.Repeat(deleted, erasedAt);
                }

                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);

                // The row's count, not this pass's, so a reissue after a lost send does not report nothing erased
                // (ADR-094).
                return seen.Count;
            });

        // Even a count of zero is reported: silence cannot be told from success (§11.7).
        await reporter.ReportAsync(integrationEvent.RequestId, erased, ct);
    }
}
