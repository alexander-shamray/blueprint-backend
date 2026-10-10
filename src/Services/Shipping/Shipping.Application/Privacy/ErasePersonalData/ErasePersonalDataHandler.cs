using Common.Application;
using Common.Domain;

namespace Shipping.Application.Privacy.ErasePersonalData;

/// <summary>Deletes the subject's delivery addresses and writes the audit row (ADR-092).</summary>
/// <remarks>A request already seen repeats rather than fails, so a reissue reports again (§11.7).</remarks>
public sealed class ErasePersonalDataHandler(
    IShippingPersonalDataStore store,
    IPersonalDataErasureRepository erasures,
    TimeProvider clock)
    : ICommandHandler<ErasePersonalDataCommand, Result<int>>
{
    public async Task<Result<int>> HandleAsync(ErasePersonalDataCommand command, CancellationToken ct)
    {
        int count = await store.DeleteAddressesAsync(command.SubjectId, ct);
        DateTimeOffset now = clock.GetUtcNow();

        PersonalDataErasure? seen = await erasures.GetAsync(command.RequestId, ct);
        if (seen is null)
            erasures.Add(PersonalDataErasure.Record(command.RequestId, command.SubjectId, count, now));
        else
            seen.Repeat(count, now);

        return Result.Success(count);
    }
}
