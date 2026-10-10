using Common.Application;
using Microsoft.Extensions.Logging;
using Privacy.Domain.ErasureRequests;

namespace Privacy.Application.ErasureRequests.MarkOverdue;

/// <summary>Marks a request overdue and says who has not answered; nothing closes it by itself (ADR-092).</summary>
public sealed class MarkErasureRequestOverdueHandler(
    IErasureRequestRepository requests,
    TimeProvider clock,
    ILogger<MarkErasureRequestOverdueHandler> log)
    : ICommandHandler<MarkErasureRequestOverdueCommand, Result>
{
    // Request id and holder names only, never the subject (§11.7). CA1848 (ADR-019).
    private static readonly Action<ILogger, Guid, string, Exception?> Overdue =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Warning,
            new EventId(1, nameof(Overdue)),
            "Request {RequestId} is overdue; no answer from {Missing}.");

    public async Task<Result> HandleAsync(MarkErasureRequestOverdueCommand command, CancellationToken ct)
    {
        ErasureRequest? request = await requests.GetLockedAsync(command.RequestId, ct);
        if (request is null)
            return Result.Failure(ErasureRequestErrors.NotFound);

        // False when it closed, was reissued or is not yet due since the sweep read it: nothing to say.
        if (request.MarkOverdue(clock.GetUtcNow()))
            Overdue(log, command.RequestId, string.Join(", ", request.Missing), null);

        return Result.Success();
    }
}
