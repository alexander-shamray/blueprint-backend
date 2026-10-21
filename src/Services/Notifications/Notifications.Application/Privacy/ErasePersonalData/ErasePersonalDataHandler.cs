using Common.Application;
using Common.Domain;

namespace Notifications.Application.Privacy.ErasePersonalData;

/// <summary>Erases the subject's notices, deliveries, records, contact, devices and consent, and audits it.</summary>
/// <remarks>
/// In this order, since the notices are found through the order records; the count is every row touched. A repeat
/// reports again (§11.7) and keeps what was written under its request since (ADR-138).
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
        (Guid subject, Guid request) = (command.SubjectId, command.RequestId);
        int count = await store.DeleteWaitingNoticesAsync(subject, request, ct) +
            await store.AnonymiseEndedNoticesAsync(subject, request, ct) +
            await store.DeleteWaitingDeliveriesAsync(subject, request, ct) +
            await store.AnonymiseEndedDeliveriesAsync(subject, request, ct) +
            await store.DeleteOrderRecordsAsync(subject, request, ct) +
            await store.DeleteContactAsync(subject, ct) +
            await store.ClearDevicesAsync(subject, request, ct) +
            await store.AnonymiseConsentAsync(subject, request, now, ct);

        PersonalDataErasure? seen = await erasures.GetAsync(command.RequestId, ct);
        if (seen is null)
        {
            seen = PersonalDataErasure.Record(command.RequestId, command.SubjectId, count, now);
            erasures.Add(seen);
        }
        else
        {
            seen.Repeat(count, now);
        }

        // The row's count, not this pass's, so a reissue after a lost send does not report nothing erased (ADR-094).
        return Result.Success(seen.Count);
    }
}
