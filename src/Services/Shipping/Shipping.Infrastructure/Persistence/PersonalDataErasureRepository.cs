using Common.Domain;
using Microsoft.EntityFrameworkCore;
using Shipping.Application.Privacy;

namespace Shipping.Infrastructure.Persistence;

internal sealed class PersonalDataErasureRepository(ShippingDbContext db) : IPersonalDataErasureRepository
{
    public Task<PersonalDataErasure?> GetAsync(Guid requestId, CancellationToken ct) =>
        db.PersonalDataErasures.FirstOrDefaultAsync(e => e.Id == requestId, ct);

    public void Add(PersonalDataErasure erasure) => db.Add(erasure);
}
