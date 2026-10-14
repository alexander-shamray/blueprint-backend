using Common.Application;

namespace Notifications.Application.Privacy.EndErasedSubject;

/// <summary>Erases the subject's notices, deliveries, order records and contact, as the erasure does (ADR-097).</summary>
/// <remarks>
/// In the erasure's order, since the notices are found through the order records (ADR-097); no audit row, as
/// the erasure that made the subject erased wrote it.
/// </remarks>
public sealed class EndErasedSubjectHandler(INotificationsPersonalDataStore store)
    : ICommandHandler<EndErasedSubjectCommand, Result>
{
    public async Task<Result> HandleAsync(EndErasedSubjectCommand command, CancellationToken ct)
    {
        await store.DeleteWaitingNoticesAsync(command.SubjectId, ct);
        await store.AnonymiseEndedNoticesAsync(command.SubjectId, ct);
        await store.DeleteWaitingDeliveriesAsync(command.SubjectId, ct);
        await store.AnonymiseEndedDeliveriesAsync(command.SubjectId, ct);
        await store.DeleteOrderRecordsAsync(command.SubjectId, ct);
        await store.DeleteContactAsync(command.SubjectId, ct);

        return Result.Success();
    }
}
