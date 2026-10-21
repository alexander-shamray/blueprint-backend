using Common.Application;
using Common.Domain;

namespace Ordering.Application.Privacy.ErasePersonalData;

/// <summary>Anonymises the subject's orders and summaries and writes the audit row (ADR-092).</summary>
/// <remarks>
/// A request already seen repeats rather than fails, so a reissue reports again (§11.7). An order placed under the
/// request is not the request's to erase, however often it is run (ADR-099).
/// </remarks>
public sealed class ErasePersonalDataHandler(
    IOrderPersonalDataStore store,
    IPersonalDataErasureRepository erasures,
    TimeProvider clock)
    : ICommandHandler<ErasePersonalDataCommand, Result<int>>
{
    public async Task<Result<int>> HandleAsync(ErasePersonalDataCommand command, CancellationToken ct)
    {
        // Before the statements, so a placement already holding it has committed its order before they run (ADR-137).
        await erasures.LockSubjectAsync(command.SubjectId, ct);

        int count = await store.AnonymiseOrdersAsync(command.SubjectId, command.RequestId, ct) +
            await store.AnonymiseSummariesAsync(command.SubjectId, command.RequestId, ct);
        DateTimeOffset now = clock.GetUtcNow();

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
