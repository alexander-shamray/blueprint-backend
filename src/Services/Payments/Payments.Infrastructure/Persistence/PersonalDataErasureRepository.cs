using Common.Domain;
using Microsoft.EntityFrameworkCore;
using Payments.Application.Privacy;

namespace Payments.Infrastructure.Persistence;

internal sealed class PersonalDataErasureRepository(PaymentsDbContext db) : IPersonalDataErasureRepository
{
    public Task<PersonalDataErasure?> GetAsync(Guid requestId, CancellationToken ct) =>
        db.PersonalDataErasures.FirstOrDefaultAsync(e => e.Id == requestId, ct);

    public void Add(PersonalDataErasure erasure) => db.Add(erasure);
}
