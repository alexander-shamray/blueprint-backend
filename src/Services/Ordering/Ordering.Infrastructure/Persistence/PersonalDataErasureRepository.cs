using Common.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Ordering.Application.Privacy;

namespace Ordering.Infrastructure.Persistence;

internal sealed class PersonalDataErasureRepository(OrderingDbContext db) : IPersonalDataErasureRepository
{
    // A name, not a range on IX_Orders_CustomerId, whose locks depend on the plan each statement gets (ADR-137).
    // Owned by the transaction, so commit or rollback releases it; outside one it is refused and THROW stops the work.
    private const string LockSubjectSql =
        """
        DECLARE @taken int;
        EXEC @taken = sp_getapplock
            @Resource = {0}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;
        IF @taken < 0 THROW 50001, 'The customer''s erasure lock was not taken.', 1;
        """;

    // The number LockSubjectSql throws with, so a lock not taken is told from any other fault.
    internal const int LockNotTakenNumber = 50001;

    public Task<PersonalDataErasure?> GetAsync(Guid requestId, CancellationToken ct) =>
        db.PersonalDataErasures.FirstOrDefaultAsync(e => e.Id == requestId, ct);

    public async Task LockSubjectAsync(Guid subjectId, CancellationToken ct)
    {
        string resource = $"ordering.erasure.subject.{subjectId:N}";

        try
        {
            await db.Database.ExecuteSqlRawAsync(LockSubjectSql, [resource], ct);
        }
        catch (SqlException e) when (e.Number == LockNotTakenNumber)
        {
            throw new CustomerLockNotTakenException(resource, e);
        }
    }

    public async Task<IReadOnlyList<Guid>> RequestsErasingAsync(Guid subjectId, CancellationToken ct)
    {
        var rows = await db.PersonalDataErasures
            .AsNoTracking()
            .Select(e => new { RequestId = e.Id, e.SubjectHash })
            .ToListAsync(ct);

        return [.. rows.Where(r => PersonalDataErasure.HashSubject(r.RequestId, subjectId) == r.SubjectHash.Trim())
            .Select(r => r.RequestId)];
    }

    public void Add(PersonalDataErasure erasure) => db.Add(erasure);
}
