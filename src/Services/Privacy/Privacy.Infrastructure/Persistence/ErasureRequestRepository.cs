using Microsoft.EntityFrameworkCore;
using Privacy.Application.ErasureRequests;
using Privacy.Domain.ErasureRequests;

namespace Privacy.Infrastructure.Persistence;

internal sealed class ErasureRequestRepository(PrivacyDbContext db) : IErasureRequestRepository
{
    // Owned by the transaction, so it is released at commit or rollback and a crash strands nothing (§6.3).
    // A negative return is a lock not taken, which must stop the raise rather than let it race (ADR-092).
    private const string TakeSubjectLockSql =
        """
        DECLARE @taken int;
        EXEC @taken = sp_getapplock
            @Resource = {0}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;
        IF @taken < 0 THROW 50001, 'The subject''s erasure lock was not taken.', 1;
        """;

    /// <summary>
    /// Serialised per subject: a second raise waits for the first to commit and then finds its request, instead of
    /// losing on the unique index with a fault (ADR-092).
    /// </summary>
    public async Task<ErasureRequest?> GetUnclosedForSubjectAsync(Guid subjectId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync(TakeSubjectLockSql, [$"privacy.erasure.subject.{subjectId:N}"], ct);

        return await db.ErasureRequests.FirstOrDefaultAsync(r => r.SubjectId == subjectId, ct);
    }

    public void Add(ErasureRequest request) => db.ErasureRequests.Add(request);
}
