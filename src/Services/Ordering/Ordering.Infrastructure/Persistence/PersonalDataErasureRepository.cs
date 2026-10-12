using Common.Domain;
using Microsoft.EntityFrameworkCore;
using Ordering.Application.Privacy;

namespace Ordering.Infrastructure.Persistence;

internal sealed class PersonalDataErasureRepository(OrderingDbContext db) : IPersonalDataErasureRepository
{
    public Task<PersonalDataErasure?> GetAsync(Guid requestId, CancellationToken ct) =>
        db.PersonalDataErasures.FirstOrDefaultAsync(e => e.Id == requestId, ct);

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
