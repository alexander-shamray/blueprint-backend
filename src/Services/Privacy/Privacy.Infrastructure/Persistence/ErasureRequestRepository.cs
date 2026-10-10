using Microsoft.EntityFrameworkCore;
using Privacy.Application.ErasureRequests;
using Privacy.Domain.ErasureRequests;

namespace Privacy.Infrastructure.Persistence;

internal sealed class ErasureRequestRepository(PrivacyDbContext db) : IErasureRequestRepository
{
    public Task<ErasureRequest?> GetUnclosedForSubjectAsync(Guid subjectId, CancellationToken ct) =>
        db.ErasureRequests.FirstOrDefaultAsync(r => r.SubjectId == subjectId, ct);

    public void Add(ErasureRequest request) => db.ErasureRequests.Add(request);
}
