using Common.Application;
using Common.Domain;

namespace Notifications.Application.Privacy.CloseErasure;

/// <summary>Records that a request Notifications answered has closed, so a check whose data is newer skips it
/// (ADR-139).</summary>
/// <remarks>A request with no row here has not been erased here yet, so there is nothing to stamp.</remarks>
public sealed class CloseErasureHandler(IPersonalDataErasureRepository erasures)
    : ICommandHandler<CloseErasureCommand, Result>
{
    public async Task<Result> HandleAsync(CloseErasureCommand command, CancellationToken ct)
    {
        PersonalDataErasure? seen = await erasures.GetAsync(command.RequestId, ct);

        seen?.MarkClosed(command.ClosedAt);

        return Result.Success();
    }
}
