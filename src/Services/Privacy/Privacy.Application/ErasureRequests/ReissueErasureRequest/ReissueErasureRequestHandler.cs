using Common.Application;
using Microsoft.Extensions.Options;
using Privacy.Domain.ErasureRequests;

namespace Privacy.Application.ErasureRequests.ReissueErasureRequest;

/// <summary>
/// Reopens a request that has not closed, with a fresh time to answer in, and so broadcasts it again (ADR-092).
/// </summary>
public sealed class ReissueErasureRequestHandler(
    IErasureRequestRepository requests,
    IOptions<PrivacyOptions> options,
    TimeProvider clock)
    : ICommandHandler<ReissueErasureRequestCommand, Result>
{
    public async Task<Result> HandleAsync(ReissueErasureRequestCommand command, CancellationToken ct)
    {
        ErasureRequest? request = await requests.GetLockedAsync(command.RequestId, ct);
        if (request is null)
            return Result.Failure(ErasureRequestErrors.NotFound);

        return request.Reissue(options.Value.CompletionSlo, clock.GetUtcNow())
            ? Result.Success()
            : Result.Failure(ErasureRequestErrors.Closed);
    }
}
