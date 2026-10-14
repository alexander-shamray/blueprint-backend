using Common.Application;
using Common.Domain;

namespace Notifications.Application.Privacy.ErasePersonalData;

/// <summary>Erases the subject's notices, records, contact, devices and consent; writes the audit row.</summary>
/// <remarks>
/// In this order, since the notices are found through the order records; the count is every row touched. A request
/// already seen repeats rather than fails, so a reissue reports again (§11.7).
/// </remarks>
public sealed class ErasePersonalDataHandler(
    INotificationsPersonalDataStore store,
    IPersonalDataErasureRepository erasures,
    TimeProvider clock)
    : ICommandHandler<ErasePersonalDataCommand, Result<int>>
{
    public async Task<Result<int>> HandleAsync(ErasePersonalDataCommand command, CancellationToken ct)
    {
        DateTimeOffset now = clock.GetUtcNow();
        int count = await store.DeleteWaitingNoticesAsync(command.SubjectId, ct) +
            await store.AnonymiseEndedNoticesAsync(command.SubjectId, ct) +
            await store.DeleteOrderRecordsAsync(command.SubjectId, ct) +
            await store.DeleteContactAsync(command.SubjectId, ct) +
            await store.ClearDevicesAsync(command.SubjectId, ct) +
            await store.AnonymiseConsentAsync(command.SubjectId, now, ct);

        PersonalDataErasure? seen = await erasures.GetAsync(command.RequestId, ct);
        if (seen is null)
            erasures.Add(PersonalDataErasure.Record(command.RequestId, command.SubjectId, count, now));
        else
            seen.Repeat(count, now);

        return Result.Success(count);
    }
}
