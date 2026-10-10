using Common.Application;
using Microsoft.Extensions.Logging;
using Privacy.Domain.ErasureRequests;

namespace Privacy.Application.ErasureRequests.RecordCompletion;

/// <summary>Counts a holder's answer, and closes the request on the last one expected (ADR-092).</summary>
public sealed class RecordErasureCompletionHandler(
    IErasureRequestRepository requests,
    TimeProvider clock,
    ILogger<RecordErasureCompletionHandler> log)
    : ICommandHandler<RecordErasureCompletionCommand, Result>
{
    // Request ids and holder names only, never the subject (§11.7). CA1848 (ADR-019).
    private static readonly Action<ILogger, Guid, string, Exception?> Stray =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Warning,
            new EventId(1, nameof(Stray)),
            "Request {RequestId} heard from {Responder}, which is not in the set it was raised with; flagged, not counted.");

    private static readonly Action<ILogger, Guid, Exception?> Closed =
        LoggerMessage.Define<Guid>(
            LogLevel.Information,
            new EventId(2, nameof(Closed)),
            "Request {RequestId} closed: every holder it was raised with has answered.");

    public async Task<Result> HandleAsync(RecordErasureCompletionCommand command, CancellationToken ct)
    {
        ErasureRequest? request = await requests.GetLockedAsync(command.RequestId, ct);
        if (request is null)
            return Result.Failure(ErasureRequestErrors.NotFound);

        CompletionOutcome outcome = request.RecordCompletion(command.Responder, command.Count, clock.GetUtcNow());

        if (outcome == CompletionOutcome.Unexpected)
            Stray(log, command.RequestId, command.Responder, null);

        if (request.Status == ErasureStatus.Closed && outcome == CompletionOutcome.Counted)
            Closed(log, command.RequestId, null);

        return Result.Success();
    }
}
