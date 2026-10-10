using Common.Domain;
using Microsoft.EntityFrameworkCore;
using Ordering.Application.Privacy;

namespace Ordering.Infrastructure.Persistence;

internal sealed class PersonalDataErasureRepository(OrderingDbContext db) : IPersonalDataErasureRepository
{
    public Task<PersonalDataErasure?> GetAsync(Guid requestId, CancellationToken ct) =>
        db.PersonalDataErasures.FirstOrDefaultAsync(e => e.Id == requestId, ct);

    public void Add(PersonalDataErasure erasure) => db.Add(erasure);
}
