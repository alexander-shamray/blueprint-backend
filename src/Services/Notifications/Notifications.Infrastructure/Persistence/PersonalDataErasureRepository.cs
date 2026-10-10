using Common.Domain;
using Microsoft.EntityFrameworkCore;
using Notifications.Application.Privacy;

namespace Notifications.Infrastructure.Persistence;

internal sealed class PersonalDataErasureRepository(NotificationsDbContext db) : IPersonalDataErasureRepository
{
    public Task<PersonalDataErasure?> GetAsync(Guid requestId, CancellationToken ct) =>
        db.PersonalDataErasures.FirstOrDefaultAsync(e => e.Id == requestId, ct);

    public void Add(PersonalDataErasure erasure) => db.Add(erasure);
}
