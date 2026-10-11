using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Privacy.Application.ErasureRequests;
using Privacy.Domain.ErasureRequests;

namespace Privacy.Infrastructure.Persistence;

internal sealed class ErasureRequestRepository(PrivacyDbContext db) : IErasureRequestRepository
{
    // Owned by the transaction, so it is released at commit or rollback and a crash strands nothing (§6.3).
    // A negative return is a lock not taken, which must stop the work rather than let it race.
    private const string TakeLockSql =
        """
        DECLARE @taken int;
        EXEC @taken = sp_getapplock
            @Resource = {0}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;
        IF @taken < 0 THROW 50001, 'The erasure lock was not taken.', 1;
        """;

    // The number TakeLockSql throws with, so a lock not taken is told from any other fault.
    internal const int LockNotTakenNumber = 50001;

    /// <summary>
    /// Serialised per subject: a second raise waits for the first to commit and then finds its request, instead of
    /// losing on the unique index with a fault (TakeLockSql).
    /// </summary>
    public async Task<ErasureRequest?> GetUnclosedForSubjectAsync(Guid subjectId, CancellationToken ct)
    {
        await TakeLockAsync($"privacy.erasure.subject.{subjectId:N}", ct);

        return await db.ErasureRequests.FirstOrDefaultAsync(r => r.SubjectId == subjectId, ct);
    }

    /// <summary>
    /// Serialised per request: the holders' answers arrive together, and each is taken in turn against the row the
    /// last one left, so none is lost to a concurrency fault and none needs a retry (TakeLockSql).
    /// </summary>
    public async Task<ErasureRequest?> GetLockedAsync(Guid requestId, CancellationToken ct)
    {
        await TakeLockAsync($"privacy.erasure.request.{requestId:N}", ct);

        return await db.ErasureRequests.FirstOrDefaultAsync(r => r.Id == requestId, ct);
    }

    public void Add(ErasureRequest request) => db.ErasureRequests.Add(request);

    private async Task TakeLockAsync(string resource, CancellationToken ct)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(TakeLockSql, [resource], ct);
        }
        catch (SqlException e) when (e.Number == LockNotTakenNumber)
        {
            throw new ErasureLockNotTakenException(resource, e);
        }
    }
}
