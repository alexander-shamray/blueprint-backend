using Common.Application;
using Microsoft.Extensions.Logging;
using Privacy.Domain.ErasureRequests;

namespace Privacy.Application.ErasureRequests.AmendResponders;

/// <summary>Changes an unclosed request's set under the caller's name, closing it when none is missing.</summary>
public sealed class AmendErasureRespondersHandler(
    IErasureRequestRepository requests,
    ICurrentUser currentUser,
    TimeProvider clock,
    ILogger<AmendErasureRespondersHandler> log)
    : ICommandHandler<AmendErasureRespondersCommand, Result>
{
    // Request ids only, never the subject or the operator (§11.7). CA1848 (ADR-019).
    private static readonly Action<ILogger, Guid, Exception?> Closed =
        LoggerMessage.Define<Guid>(
            LogLevel.Information,
            new EventId(1, nameof(Closed)),
            "Request {RequestId} closed: every holder left in its amended set has answered.");

    public async Task<Result> HandleAsync(AmendErasureRespondersCommand command, CancellationToken ct)
    {
        ErasureRequest? request = await requests.GetLockedAsync(command.RequestId, ct);
        if (request is null)
            return Result.Failure(ErasureRequestErrors.NotFound);

        if (request.Status == ErasureStatus.Closed)
            return Result.Failure(ErasureRequestErrors.Closed);

        string? unfit = request.WhyNotAnAmendment(command.Add, command.Remove);
        if (unfit is not null)
            return Result.Failure(ErasureRequestErrors.AmendmentRefused(unfit));

        if (request.AmendResponders(command.Add, command.Remove, command.Reason, currentUser.Id, clock.GetUtcNow()))
            Closed(log, command.RequestId, null);

        return Result.Success();
    }
}
