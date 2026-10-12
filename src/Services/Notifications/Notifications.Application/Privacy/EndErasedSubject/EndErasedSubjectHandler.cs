using Common.Application;

namespace Notifications.Application.Privacy.EndErasedSubject;

/// <summary>Deletes the subject's waiting notices, order records and contact, as the erasure does (ADR-097).</summary>
/// <remarks>
/// In the erasure's order, since the notices are found through the order records. It writes no audit row: the
/// erasure that made the subject erased already did.
/// </remarks>
public sealed class EndErasedSubjectHandler(INotificationsPersonalDataStore store)
    : ICommandHandler<EndErasedSubjectCommand, Result>
{
    public async Task<Result> HandleAsync(EndErasedSubjectCommand command, CancellationToken ct)
    {
        await store.DeleteWaitingNoticesAsync(command.SubjectId, ct);
        await store.DeleteOrderRecordsAsync(command.SubjectId, ct);
        await store.DeleteContactAsync(command.SubjectId, ct);

        return Result.Success();
    }
}
