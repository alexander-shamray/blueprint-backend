using Common.Application;
using Common.Contracts.Privacy.V1;
using Common.Domain;
using Microsoft.EntityFrameworkCore;
using Web.Bff.Persistence;

namespace Web.Bff.Privacy;

/// <summary>Deletes the subject's order rows and lines, audits it, then tells Privacy once it commits (ADR-092).</summary>
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
        int erased = await db.Database.CreateExecutionStrategy().ExecuteAsync(
            async () =>
            {
                // A re-run starts from nothing, so an audit row the failed attempt tracked is not added twice.
                db.ChangeTracker.Clear();

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                // The lines go with their order, by the foreign key's cascade (ADR-051).
                int deleted = await db.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM bff.Orders WHERE CustomerId = {integrationEvent.SubjectId}",
                    ct);

                PersonalDataErasure? seen = await db.PersonalDataErasures
                    .FirstOrDefaultAsync(e => e.Id == integrationEvent.RequestId, ct);

                DateTimeOffset now = clock.GetUtcNow();
                if (seen is null)
                {
                    db.PersonalDataErasures.Add(
                        PersonalDataErasure.Record(integrationEvent.RequestId, integrationEvent.SubjectId, deleted, now));
                }
                else
                {
                    seen.Repeat(deleted, now);
                }

                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);

                return deleted;
            });

        // Even a count of zero is reported: silence cannot be told from success (§11.7).
        await reporter.ReportAsync(integrationEvent.RequestId, erased, ct);
    }
}
